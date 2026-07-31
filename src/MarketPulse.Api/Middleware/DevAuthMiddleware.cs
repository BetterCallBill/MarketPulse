using System.Security.Claims;
using MarketPulse.Infrastructure.Persistence;

namespace MarketPulse.Api.Middleware;

/// <summary>
/// Development-only identity stub. Slice 2 deletes this file and issues real JWTs;
/// no schema, query, or handler changes are needed when it goes.
/// </summary>
public sealed class DevAuthMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DevAuthMiddleware must never run outside the Development environment.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, SeedData.DevUserId.ToString())],
            authenticationType: "DevAuth");

        context.User = new ClaimsPrincipal(identity);

        await next(context);
    }
}
