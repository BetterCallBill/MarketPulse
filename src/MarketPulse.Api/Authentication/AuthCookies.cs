namespace MarketPulse.Api.Authentication;

/// <summary>
/// Cookie names and the one place their security attributes are decided. Kept as a pure
/// function so the attributes are unit-testable without spinning up a host.
/// </summary>
public static class AuthCookies
{
    public const string Access = "mp_access";
    public const string Refresh = "mp_refresh";
    public const string Csrf = "mp_csrf";

    /// <summary>
    /// The refresh cookie is scoped to the one endpoint that consumes it, so it is not
    /// transmitted on any other request.
    /// </summary>
    public const string RefreshPath = "/api/v1/auth/refresh";

    public static CookieOptions Build(
        bool isDevelopment,
        SameSiteMode sameSite,
        DateTimeOffset expires,
        string? path) => new()
    {
        HttpOnly = true,

        // Secure is off in Development on purpose: localhost is served over plain HTTP,
        // and .NET's CookieContainer will not send a Secure cookie over HTTP, which would
        // silently break the integration suite.
        Secure = !isDevelopment,
        SameSite = sameSite,
        Expires = expires,
        Path = path ?? "/"
    };
}
