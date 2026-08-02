using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// Registers a fresh user and returns a client whose cookie jar holds that session,
/// with the CSRF header pre-attached so mutations pass the double-submit check.
/// </summary>
public static class AuthenticatedClient
{
    public const string ValidPassword = "correct horse battery staple";

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@marketpulse.local";

    public static async Task<HttpClient> RegisterAsync(
        WebApplicationFactory<Program> factory, string? email = null)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { Email = email ?? NewEmail(), Password = ValidPassword });

        response.EnsureSuccessStatusCode();
        AttachCsrfHeader(client, response);
        return client;
    }

    public static async Task<HttpClient> LoginAsync(
        WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { Email = email, Password = password });

        response.EnsureSuccessStatusCode();
        AttachCsrfHeader(client, response);
        return client;
    }

    /// <summary>
    /// Registers a user and returns the raw `mp_access=…` cookie pair, for callers that
    /// need to set a Cookie header by hand rather than use an HttpClient cookie jar.
    /// </summary>
    public static async Task<string> RegisterAndGetAccessCookieAsync(
        WebApplicationFactory<Program> factory)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/register",
            new { Email = NewEmail(), Password = ValidPassword });

        response.EnsureSuccessStatusCode();

        return response.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';')[0])
            .Single(c => c.StartsWith("mp_access=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads the mp_csrf value out of the Set-Cookie headers and sets it as the
    /// X-CSRF-Token header — exactly what the browser client does in JavaScript.
    /// </summary>
    public static void AttachCsrfHeader(HttpClient client, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return;
        }

        var csrf = cookies
            .Select(c => c.Split(';')[0])
            .FirstOrDefault(c => c.StartsWith("mp_csrf=", StringComparison.Ordinal))
            ?["mp_csrf=".Length..];

        if (!string.IsNullOrEmpty(csrf))
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-Token");
            client.DefaultRequestHeaders.Add("X-CSRF-Token", Uri.UnescapeDataString(csrf));
        }
    }
}
