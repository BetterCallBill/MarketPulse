using System.Security.Cryptography;
using System.Text;
using MarketPulse.Api.Authentication;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Api.Middleware;

/// <summary>
/// Double-submit cookie CSRF defence: an unsafe request must echo the (JS-readable)
/// mp_csrf cookie back in a header. A cross-site attacker can cause the browser to send
/// the cookie but cannot read it to set the header.
///
/// SameSite already blocks the common cases — this is defence in depth, not the primary
/// control. Chosen over the framework's IAntiforgery because that is oriented around MVC
/// form posts rather than a JSON API.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CSRF-Token";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    /// <summary>
    /// Login and register are exempt: a client cannot hold a CSRF cookie before its first
    /// successful authentication. Both are rate limited instead.
    ///
    /// /hubs/prices is exempt too, for a different reason: negotiate is a POST, but PriceHub
    /// only ever pushes ticks server-to-client — there is no client-invoked mutation for a
    /// forged request to trigger, so there is nothing here for CSRF to protect. It is also
    /// cookie-authenticated the same way as everything else (see OnMessageReceived), and the
    /// browser's WebSocket API cannot attach a custom header to the upgrade request even if
    /// we wanted one there. This reasoning is per-hub, not a blanket exemption for anything
    /// under /hubs — a future hub with client-invokable methods needs its own path added
    /// here only after the same argument is re-checked against it.
    /// </summary>
    private static readonly string[] ExemptPaths =
        ["/api/v1/auth/login", "/api/v1/auth/register", "/hubs/prices"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method)
            || ExemptPaths.Any(p => context.Request.Path.StartsWithSegments(p)))
        {
            await next(context);
            return;
        }

        var cookie = context.Request.Cookies[AuthCookies.Csrf];
        var header = context.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header) || !Matches(cookie, header))
        {
            // Thrown rather than written directly so ExceptionHandlingMiddleware, which sits
            // above this in the pipeline, produces the same ProblemDetails shape as
            // everything else.
            throw new CsrfValidationException();
        }

        await next(context);
    }

    private static bool Matches(string cookie, string header) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header));
}
