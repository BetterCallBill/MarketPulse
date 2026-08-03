using System.Net;
using System.Net.Http.Json;
using MarketPulse.Application.Watchlists;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class CsrfAndRateLimitTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Each test builds its own factory so it can dial the thresholds down. Sharing one
    /// rate-limiter partition across tests makes them order-dependent.
    /// </summary>
    private WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?>? overrides = null) =>
        TestFactory.Create(fixture, overrides);

    [Fact]
    public async Task A_mutation_without_the_csrf_header_is_rejected_with_403()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);

        // Drop the header the helper attached — cookies alone must not be enough.
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");

        var response = await client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("csrf-failed", body!["title"].ToString());
    }

    [Fact]
    public async Task A_mutation_with_a_short_mismatched_csrf_header_is_rejected_with_403()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);

        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", "not-the-right-nonce");

        var response = await client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("csrf-failed", body!["title"].ToString());
    }

    /// <summary>
    /// A header the same length as the real nonce, differing only in its first character.
    /// <c>CryptographicOperations.FixedTimeEquals</c> short-circuits on a length mismatch, so
    /// the short-token case above never touches the actual constant-time byte comparison —
    /// this is the one that does.
    /// </summary>
    [Fact]
    public async Task A_mutation_with_a_same_length_mismatched_csrf_header_is_rejected_with_403()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var real = client.DefaultRequestHeaders.GetValues("X-CSRF-Token").Single();
        var tampered = (real[0] == 'A' ? 'B' : 'A') + real[1..];

        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", tampered);

        var response = await client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("csrf-failed", body!["title"].ToString());
    }

    [Fact]
    public async Task A_mutation_with_the_matching_csrf_header_succeeds()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Reads_do_not_require_a_csrf_header()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");

        var response = await client.GetAsync("/api/v1/watchlist");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_does_not_require_a_csrf_header()
    {
        // The client cannot have a CSRF cookie before its first successful login.
        using var factory = CreateFactory();
        var email = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(factory, email);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_sixth_bad_password_locks_the_account_with_a_retry_after()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Auth:MaxFailedAttempts"] = "5",
            ["Auth:LockoutDuration"] = "00:15:00",
            ["Auth:LoginRequestsPerMinute"] = "1000"
        });

        var email = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(factory, email);
        var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { Email = email, Password = "wrong password entirely" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Even the correct password is now refused.
        var locked = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
    }

    [Fact]
    public async Task The_login_endpoint_is_rate_limited_per_ip()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Auth:LoginRequestsPerMinute"] = "3"
        });

        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { Email = "nobody@marketpulse.local", Password = "wrong password entirely" });
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
