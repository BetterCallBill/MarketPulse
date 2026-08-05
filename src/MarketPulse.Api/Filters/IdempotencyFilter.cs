using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPulse.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MarketPulse.Api.Filters;

/// <summary>
/// Stored-key idempotency for POSTs that opt in via [ServiceFilter]. Claimed-first: the
/// key row is inserted before the action runs, so two racing requests cannot both
/// execute — the unique index picks the winner and the loser answers 409 until the
/// winner's response is stored. Failures release the claim: a 4xx/5xx stores nothing and
/// the same key may be retried. The hash is over the bound action arguments, not the raw
/// body, so formatting differences don't defeat replay while a genuinely different
/// request under a reused key is caught.
/// </summary>
public sealed class IdempotencyFilter(IIdempotencyStore store, ICurrentUser user)
    : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var values)
            || values.ToString() is not { Length: > 0 and <= 128 } key)
        {
            await next(); // the header is an offer, not a demand
            return;
        }

        var endpoint = $"{context.HttpContext.Request.Method} {context.ActionDescriptor.AttributeRouteInfo?.Template}";
        var hash = HashArguments(context.ActionArguments);

        var existing = await store.FindAsync(user.UserId, endpoint, key, context.HttpContext.RequestAborted);

        if (existing is not null)
        {
            if (existing.RequestHash != hash)
            {
                context.Result = Problem(422, "idempotency-key-reuse",
                    "This Idempotency-Key was already used with a different request.");
                return;
            }

            if (!existing.IsCompleted)
            {
                context.Result = Problem(409, "idempotency-in-flight",
                    "The original request with this key is still executing. Retry shortly.");
                return;
            }

            context.Result = new ContentResult
            {
                StatusCode = existing.ResponseStatusCode,
                Content = existing.ResponseBody,
                ContentType = "application/json"
            };
            return;
        }

        var claim = new Domain.Entities.IdempotencyKey(
            user.UserId, endpoint, key, hash, DateTimeOffset.UtcNow);

        if (!await store.TryClaimAsync(claim, context.HttpContext.RequestAborted))
        {
            // Lost the race for a fresh key. The winner is executing or done; re-reading
            // now either replays or reports in-flight — one recursive pass handles both.
            await OnActionExecutionAsync(context, next);
            return;
        }

        try
        {
            var executed = await next();

            if (executed.Exception is not null && !executed.ExceptionHandled)
            {
                await store.RemoveAsync(claim, CancellationToken.None);
                return; // the middleware turns the exception into its ProblemDetails
            }

            if (executed.Result is ObjectResult { StatusCode: null or >= 200 and < 300 } ok)
            {
                var body = JsonSerializer.Serialize(
                    ok.Value, context.HttpContext.RequestServices
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
                        .Value.JsonSerializerOptions);
                await store.CompleteAsync(
                    claim, ok.StatusCode ?? StatusCodes.Status200OK, body, CancellationToken.None);
            }
            else
            {
                // Non-2xx result (validation short-circuit, 4xx ObjectResult): release.
                await store.RemoveAsync(claim, CancellationToken.None);
            }
        }
        catch
        {
            await store.RemoveAsync(claim, CancellationToken.None);
            throw;
        }
    }

    private static string HashArguments(IDictionary<string, object?> arguments)
    {
        // CancellationToken is bound as an action argument on every action here (it's not
        // part of the request body or route) but isn't JSON-serializable — it carries a
        // WaitHandle whose IntPtr handle System.Text.Json refuses outright. It also carries
        // no information about what the caller asked for, so it plays no part in the hash.
        var canonical = JsonSerializer.Serialize(
            arguments
                .Where(a => a.Value is not CancellationToken)
                .OrderBy(a => a.Key)
                .ToDictionary(a => a.Key, a => a.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static ObjectResult Problem(int status, string code, string detail) =>
        new(new ProblemDetails
        {
            Status = status,
            Title = code,
            Type = $"https://marketpulse.local/errors/{code}",
            Detail = detail
        })
        { StatusCode = status };
}
