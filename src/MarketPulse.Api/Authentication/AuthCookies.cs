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
    /// The refresh cookie is scoped to the auth endpoints, so it never rides a watchlist,
    /// hub, or health request. It cannot be narrowed to `/api/v1/auth/refresh`: RFC 6265
    /// path-matching would then withhold it from `/api/v1/auth/logout`, so logout could
    /// never see the token it has to revoke, and every issued refresh token would stay
    /// live for its full lifetime after signing out.
    /// </summary>
    public const string RefreshPath = "/api/v1/auth";

    public static CookieOptions Build(
        bool isDevelopment,
        SameSiteMode sameSite,
        DateTimeOffset expires,
        string? path,
        bool httpOnly = true) => new()
    {
        HttpOnly = httpOnly,

        // Secure is off in Development on purpose: localhost is served over plain HTTP,
        // and .NET's CookieContainer will not send a Secure cookie over HTTP, which would
        // silently break the integration suite.
        Secure = !isDevelopment,
        SameSite = sameSite,
        Expires = expires,
        Path = path ?? "/"
    };
}
