using System.Security.Claims;
using System.Security.Cryptography;
using MarketPulse.Api.Authentication;
using MarketPulse.Application.Authentication;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MarketPulse.Api.Controllers;

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record SessionResponse(Guid Id, string Email);

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    ISender sender,
    IHostEnvironment environment) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<SessionResponse>> Register(
        [FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new RegisterUserCommand(request.Email, request.Password), ct);
        return IssueSession(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<SessionResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LoginCommand(request.Email, request.Password), ct);
        return IssueSession(result);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<SessionResponse>> Refresh(CancellationToken ct)
    {
        var token = Request.Cookies[AuthCookies.Refresh] ?? string.Empty;
        var result = await sender.Send(new RefreshSessionCommand(token), ct);
        return IssueSession(result);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await sender.Send(new LogoutCommand(Request.Cookies[AuthCookies.Refresh]), ct);
        ClearSession();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<SessionResponse> Me()
    {
        var id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = User.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;

        return Guid.TryParse(id, out var userId)
            ? Ok(new SessionResponse(userId, email))
            : Unauthorized();
    }

    private ActionResult<SessionResponse> IssueSession(AuthResult result)
    {
        var isDev = environment.IsDevelopment();

        Response.Cookies.Append(
            AuthCookies.Access, result.AccessToken,
            AuthCookies.Build(isDev, SameSiteMode.Lax, result.AccessExpiresUtc, path: null));

        Response.Cookies.Append(
            AuthCookies.Refresh, result.RefreshToken,
            AuthCookies.Build(isDev, SameSiteMode.Strict, result.RefreshExpiresUtc,
                AuthCookies.RefreshPath));

        // The CSRF nonce must outlive the access token, or a client whose access token has
        // expired would have no way to authenticate its own refresh call. It tracks the
        // refresh lifetime instead, and is readable by JavaScript by design — that is what
        // "double submit" means.
        var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Response.Cookies.Append(AuthCookies.Csrf, csrf, new CookieOptions
        {
            HttpOnly = false,
            Secure = !isDev,
            SameSite = SameSiteMode.Lax,
            Expires = result.RefreshExpiresUtc,
            Path = "/"
        });

        return Ok(new SessionResponse(result.UserId, result.Email));
    }

    private void ClearSession()
    {
        var isDev = environment.IsDevelopment();
        var expired = DateTimeOffset.UnixEpoch;

        Response.Cookies.Append(AuthCookies.Access, string.Empty,
            AuthCookies.Build(isDev, SameSiteMode.Lax, expired, path: null));
        Response.Cookies.Append(AuthCookies.Refresh, string.Empty,
            AuthCookies.Build(isDev, SameSiteMode.Strict, expired, AuthCookies.RefreshPath));
        Response.Cookies.Append(AuthCookies.Csrf, string.Empty, new CookieOptions
        {
            HttpOnly = false,
            Secure = !isDev,
            SameSite = SameSiteMode.Lax,
            Expires = expired,
            Path = "/"
        });
    }
}
