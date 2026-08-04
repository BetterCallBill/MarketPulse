using System.Net;
using System.Net.Http.Json;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class AlertsApiTests(SqlServerFixture fixture)
{
    private sealed record RuleResponse(
        Guid Id, string Ticker, string Direction, decimal Threshold, string Status);

    private static object NewRule(
        string ticker = "IVV", string direction = "Above", decimal threshold = 50m) =>
        new { Ticker = ticker, Direction = direction, Threshold = threshold };

    [Fact]
    public async Task A_rule_can_be_created_and_listed()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var created = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The Location header must be a URL that means what it says. It used to be
        // `/api/v1/alerts?id=<guid>` — CreatedAtAction pointing a route value at the
        // collection action, which has no `id` parameter to bind it to — so a client that
        // followed it got every rule the user has and no indication that the query string had
        // been ignored. There is no GET /alerts/{id} in this slice's API surface, so the
        // collection, without the invented route value, is the honest answer.
        Assert.Equal("/api/v1/alerts", created.Headers.Location?.PathAndQuery);

        var rules = await client.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts");

        var rule = Assert.Single(rules!);
        Assert.Equal("IVV", rule.Ticker);
        Assert.Equal("Active", rule.Status);
    }

    [Fact]
    public async Task An_unknown_ticker_is_rejected()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync("/api/v1/alerts", NewRule(ticker: "ZZZZ"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_threshold_of_zero_is_rejected()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync("/api/v1/alerts", NewRule(threshold: 0m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_identical_active_rule_is_a_conflict()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var second = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task The_twenty_first_rule_is_a_conflict()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        // Twenty distinct thresholds, so each one clears the duplicate check.
        for (var i = 1; i <= 20; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/alerts", NewRule(threshold: i));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var overflow = await client.PostAsJsonAsync("/api/v1/alerts", NewRule(threshold: 21m));

        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode);
    }

    [Fact]
    public async Task A_rule_can_be_deleted()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var created = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var rule = await created.Content.ReadFromJsonAsync<RuleResponse>();

        var deleted = await client.DeleteAsync($"/api/v1/alerts/{rule!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var rules = await client.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts");
        Assert.Empty(rules!);
    }

    [Fact]
    public async Task Re_arming_a_rule_that_never_fired_is_a_conflict()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var created = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var rule = await created.Content.ReadFromJsonAsync<RuleResponse>();

        var rearm = await client.PostAsync($"/api/v1/alerts/{rule!.Id}/rearm", null);

        Assert.Equal(HttpStatusCode.Conflict, rearm.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        await using var factory = TestFactory.Create(fixture);

        var response = await factory.CreateClient().GetAsync("/api/v1/alerts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task One_user_cannot_see_or_touch_another_users_rule()
    {
        await using var factory = TestFactory.Create(fixture);
        var alice = await AuthenticatedClient.RegisterAsync(factory);
        var bob = await AuthenticatedClient.RegisterAsync(factory);

        var created = await alice.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var rule = await created.Content.ReadFromJsonAsync<RuleResponse>();

        Assert.Empty((await bob.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts"))!);

        // 404, not 403: confirming the id exists would be an information leak.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.DeleteAsync($"/api/v1/alerts/{rule!.Id}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/v1/alerts/{rule.Id}/rearm", null)).StatusCode);

        // And Alice's rule is still there.
        Assert.Single((await alice.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts"))!);
    }
}
