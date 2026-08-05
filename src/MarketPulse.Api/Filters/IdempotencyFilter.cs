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
/// winner's response is stored. Failures of the <em>action</em> release the claim: a
/// 4xx/5xx from the action itself stores nothing and the same key may be retried. A
/// failure that happens after the action already succeeded — completion's own
/// serialization or store write throwing — does NOT release the claim: the action's
/// effect (e.g. a committed trade) already happened, so removing the claim would let an
/// honest retry re-execute it and produce a silent duplicate. That failure is logged and
/// the claim is left in-flight instead, so a retry degrades to 409
/// <c>idempotency-in-flight</c> — the orphaned-claim outcome ADR-004's consequences
/// already documents — never a duplicate. The hash is over the bound action arguments,
/// not the raw body, so formatting differences don't defeat replay while a genuinely
/// different request under a reused key is caught.
/// </summary>
public sealed class IdempotencyFilter(
    IIdempotencyStore store, ICurrentUser user, ILogger<IdempotencyFilter> logger)
    : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>
    /// Caps the lost-race re-check so a pathological interleaving of many racers on the
    /// same fresh key can't turn into unbounded looping. In practice this converges in
    /// one or two passes: after losing the claim, the winner is either already done
    /// (replay) or still running (409 in-flight).
    /// </summary>
    private const int MaxClaimAttempts = 5;

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
        var ct = context.HttpContext.RequestAborted;

        Domain.Entities.IdempotencyKey? claim = null;

        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            var existing = await store.FindAsync(user.UserId, endpoint, key, ct);

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

            var candidate = new Domain.Entities.IdempotencyKey(
                user.UserId, endpoint, key, hash, DateTimeOffset.UtcNow);

            if (await store.TryClaimAsync(candidate, ct))
            {
                claim = candidate;
                break;
            }

            // Lost the race for a fresh key. The winner is executing or done; loop back
            // and re-read — that either replays its response or reports in-flight.
        }

        if (claim is null)
        {
            // Exhausted the bounded re-check under pathological contention. Treat it the
            // same as "still executing" rather than falling through uncontrolled — safe
            // for an honest retry either way.
            context.Result = Problem(409, "idempotency-in-flight",
                "The original request with this key is still executing. Retry shortly.");
            return;
        }

        ActionExecutedContext executed;
        try
        {
            executed = await next();
        }
        catch
        {
            // next() itself throwing (rather than the action's exception surfacing on
            // executed.Exception) means the action pipeline never produced an observable
            // result at all — nothing was committed on this filter's behalf. Safe to
            // release.
            await store.RemoveAsync(claim, CancellationToken.None);
            throw;
        }

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            // The action itself failed. Release: nothing it did should be retried under
            // this key, so the same key may be reused for a fresh attempt.
            await store.RemoveAsync(claim, CancellationToken.None);
            return; // the middleware turns the exception into its ProblemDetails
        }

        if (executed.Result is ObjectResult { StatusCode: null or >= 200 and < 300 } ok)
        {
            // NOTE: only ObjectResult is handled here. A future 2xx endpoint returning a
            // non-ObjectResult (e.g. NoContentResult) will fall through to "else" below
            // and have its claim incorrectly released — add a branch here first.
            try
            {
                var body = JsonSerializer.Serialize(
                    ok.Value, context.HttpContext.RequestServices
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
                        .Value.JsonSerializerOptions);
                await store.CompleteAsync(
                    claim, ok.StatusCode ?? StatusCodes.Status200OK, body, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // The action already succeeded — its effect (e.g. a committed trade) is
                // real. Do NOT remove the claim: that would let an honest same-key retry
                // re-execute an already-committed action, a silent duplicate. Leave the
                // claim in-flight; the retry gets 409 idempotency-in-flight instead. The
                // caller's own response (executed.Result) is untouched and still flows
                // through normally — only this filter's own bookkeeping failed.
                logger.LogWarning(ex,
                    "Idempotency claim {ClaimId} for {Endpoint} left in-flight: " +
                    "storing the response failed after the action already succeeded.",
                    claim.Id, endpoint);
            }
        }
        else
        {
            // Non-2xx result (validation short-circuit, 4xx ObjectResult): release.
            await store.RemoveAsync(claim, CancellationToken.None);
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
