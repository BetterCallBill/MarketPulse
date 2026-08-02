using System.Net;
using System.Net.Http.Json;
using MarketPulse.Api.Controllers;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class AuthApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);
            b.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(DbContextOptions<MarketPulseDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Registering_sets_all_three_cookies()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = AuthenticatedClient.ValidPassword });

        response.EnsureSuccessStatusCode();
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        Assert.Contains(cookies, c => c.StartsWith("mp_access=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("mp_refresh=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("mp_csrf=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_access_and_refresh_cookies_are_http_only_and_the_csrf_cookie_is_not()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = AuthenticatedClient.ValidPassword });

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        Assert.Contains(cookies, c => c.StartsWith("mp_access=", StringComparison.Ordinal)
                                      && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.StartsWith("mp_refresh=", StringComparison.Ordinal)
                                      && c.Contains("path=/api/v1/auth", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.StartsWith("mp_csrf=", StringComparison.Ordinal)
                                      && !c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_weak_password_is_rejected_with_400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registering_the_same_email_twice_returns_409()
    {
        var client = _factory.CreateClient();
        var email = AuthenticatedClient.NewEmail();

        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        var second = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("email-taken", body!["title"].ToString());
    }

    [Fact]
    public async Task Me_returns_the_registered_user()
    {
        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(_factory, email);

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/me");

        Assert.NotNull(session);
        Assert.Equal(email, session!.Email);
        Assert.NotEqual(Guid.Empty, session.Id);
    }

    [Fact]
    public async Task Me_without_a_cookie_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("unauthenticated", body!["title"].ToString());
    }

    [Fact]
    public async Task The_seeded_dev_user_can_sign_in()
    {
        var client = await AuthenticatedClient.LoginAsync(
            _factory, SeedData.DevUserEmail, SeedData.DevUserPassword);

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/me");

        Assert.Equal(SeedData.DevUserId, session!.Id);
    }

    [Fact]
    public async Task A_wrong_password_returns_401_with_a_generic_message()
    {
        var email = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(_factory, email);

        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new { Email = email, Password = "definitely wrong password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-credentials", body!["title"].ToString());
    }

    [Fact]
    public async Task An_unknown_email_returns_the_same_error_as_a_wrong_password()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { Email = "nobody-at-all@marketpulse.local", Password = "definitely wrong password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-credentials", body!["title"].ToString());
    }

    [Fact]
    public async Task Refreshing_rotates_the_refresh_cookie()
    {
        var client = await AuthenticatedClient.RegisterAsync(_factory);

        var response = await client.PostAsync("/api/v1/auth/refresh", content: null);

        response.EnsureSuccessStatusCode();
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("mp_refresh=", StringComparison.Ordinal));

        // The session still works after rotation.
        AuthenticatedClient.AttachCsrfHeader(client, response);
        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/me");
        Assert.NotNull(session);
    }

    [Fact]
    public async Task Logging_out_clears_the_session()
    {
        var client = await AuthenticatedClient.RegisterAsync(_factory);

        var logout = await client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var after = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    /// <summary>
    /// The control for the test below. A captured refresh token replayed from a client with
    /// an empty cookie jar must genuinely reach the server and mint a session — otherwise
    /// the revocation test could pass for the wrong reason, because a refresh request that
    /// carried no cookie at all would also fail.
    /// </summary>
    [Fact]
    public async Task A_captured_refresh_token_works_from_a_clean_client()
    {
        var registered = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = AuthenticatedClient.ValidPassword });
        registered.EnsureSuccessStatusCode();

        var captured = AuthenticatedClient.ReadCookie(registered, "mp_refresh");
        var csrf = AuthenticatedClient.ReadCookie(registered, "mp_csrf");
        Assert.False(string.IsNullOrEmpty(captured));

        // Refresh is a mutation like any other, so the replayed request also has to carry
        // the matching CSRF cookie/header pair — the captured mp_refresh cookie alone is not
        // enough, by design.
        var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replay.Headers.Add("Cookie", $"mp_refresh={captured}; mp_csrf={csrf}");
        replay.Headers.Add("X-CSRF-Token", Uri.UnescapeDataString(csrf!));

        var response = await _factory.CreateClient().SendAsync(replay);

        Assert.True(response.IsSuccessStatusCode,
            "A hand-attached Cookie header must reach the refresh endpoint.");
    }

    /// <summary>
    /// Logout has to revoke server-side, not just clear the caller's cookies. Asserting the
    /// caller's own jar was emptied is exactly what made the original defect look fixed: the
    /// refresh cookie was path-scoped to /api/v1/auth/refresh, never rode the logout request,
    /// and LogoutHandler's null guard meant RevokeAllForUserAsync was unreachable — so a
    /// token captured beforehand stayed valid for its full 14 days.
    /// </summary>
    [Fact]
    public async Task Logging_out_revokes_a_refresh_token_captured_beforehand()
    {
        var client = _factory.CreateClient();

        var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = AuthenticatedClient.ValidPassword });
        registered.EnsureSuccessStatusCode();

        var captured = AuthenticatedClient.ReadCookie(registered, "mp_refresh");
        var csrf = AuthenticatedClient.ReadCookie(registered, "mp_csrf");
        Assert.False(string.IsNullOrEmpty(captured));

        // client's cookie jar already holds mp_csrf from registration; logout is a mutation,
        // so it also needs the matching header attached.
        AuthenticatedClient.AttachCsrfHeader(client, registered);
        var logout = await client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // A clean client carrying nothing but the captured token — no access cookie, and
        // nothing inherited from the jar that logout just cleared. The CSRF pair is the
        // same one captured at registration: CSRF validation is a stateless cookie/header
        // compare, unrelated to whether the session it accompanies has since been revoked.
        var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replay.Headers.Add("Cookie", $"mp_refresh={captured}; mp_csrf={csrf}");
        replay.Headers.Add("X-CSRF-Token", Uri.UnescapeDataString(csrf!));

        var response = await _factory.CreateClient().SendAsync(replay);

        Assert.False(response.IsSuccessStatusCode,
            "A refresh token captured before logout must not still mint a session.");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_reachable_without_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }
}
