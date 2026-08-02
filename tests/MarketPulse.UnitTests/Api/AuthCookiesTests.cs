using MarketPulse.Api.Authentication;
using Microsoft.AspNetCore.Http;

namespace MarketPulse.UnitTests.Api;

public class AuthCookiesTests
{
    private static readonly DateTimeOffset Expires = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Outside_development_cookies_are_secure()
    {
        var options = AuthCookies.Build(isDevelopment: false, SameSiteMode.Lax, Expires, path: null);

        Assert.True(options.Secure);
    }

    [Fact]
    public void In_development_cookies_are_not_secure()
    {
        // .NET's CookieContainer refuses to send Secure cookies over plain HTTP, which
        // would break every integration test and the local dev loop.
        var options = AuthCookies.Build(isDevelopment: true, SameSiteMode.Lax, Expires, path: null);

        Assert.False(options.Secure);
    }

    [Fact]
    public void Cookies_are_http_only_by_default()
    {
        var options = AuthCookies.Build(isDevelopment: true, SameSiteMode.Lax, Expires, path: null);

        Assert.True(options.HttpOnly);
    }

    [Fact]
    public void The_path_is_applied_when_supplied()
    {
        var options = AuthCookies.Build(
            isDevelopment: true, SameSiteMode.Strict, Expires, AuthCookies.RefreshPath);

        Assert.Equal("/api/v1/auth/refresh", options.Path);
        Assert.Equal(SameSiteMode.Strict, options.SameSite);
    }

    [Fact]
    public void The_refresh_cookie_is_scoped_to_the_refresh_endpoint()
    {
        Assert.Equal("/api/v1/auth/refresh", AuthCookies.RefreshPath);
    }
}
