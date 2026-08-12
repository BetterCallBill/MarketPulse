using System.Net;
using System.Net.Http.Json;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class NotificationsApiTests(SqlServerFixture fixture)
{
    private sealed record NotificationResponse(
        Guid Id, string Ticker, decimal TriggeredPrice, bool IsRead);

    /// <summary>
    /// Seeds a notification directly. Task 9 wires the real path; this test is about the
    /// read side, and going through the broker to test a GET would be a worse test.
    /// </summary>
    private async Task<Guid> SeedAsync(string email, DateTimeOffset occurredUtc)
    {
        await using var db = fixture.CreateContext();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();

        var notification = Notification.Create(
            Guid.NewGuid(), userId, Guid.NewGuid(), "IVV",
            AlertDirection.Above, 50m, 51m, occurredUtc, occurredUtc);

        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return notification.Id;
    }

    [Fact]
    public async Task Notifications_come_back_newest_first()
    {
        await using var factory = TestFactory.Create(fixture);
        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(factory, email);

        var older = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedAsync(email, older);
        var newerId = await SeedAsync(email, older.AddHours(1));

        var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications");

        Assert.Equal(2, list!.Count);
        Assert.Equal(newerId, list[0].Id);
    }

    [Fact]
    public async Task Take_is_capped_at_one_hundred()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        // An uncapped take is a denial-of-service handed to any authenticated caller.
        var response = await client.GetAsync("/api/v1/notifications?take=5000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_notification_can_be_marked_read()
    {
        await using var factory = TestFactory.Create(fixture);
        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(factory, email);

        var id = await SeedAsync(email, DateTimeOffset.UtcNow);

        var marked = await client.PostAsync($"/api/v1/notifications/{id}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);

        var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications");

        Assert.True(Assert.Single(list!).IsRead);
    }

    [Fact]
    public async Task One_user_cannot_see_or_read_another_users_notification()
    {
        await using var factory = TestFactory.Create(fixture);
        var aliceEmail = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(factory, aliceEmail);
        var bob = await AuthenticatedClient.RegisterAsync(factory);

        var id = await SeedAsync(aliceEmail, DateTimeOffset.UtcNow);

        Assert.Empty((await bob.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications"))!);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/v1/notifications/{id}/read", null)).StatusCode);
    }
}
