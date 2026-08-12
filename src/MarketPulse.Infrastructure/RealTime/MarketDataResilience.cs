using System.Net;
using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The pipeline ADR-010 describes, defined once and testable without DI. Order matters:
/// retry outermost (a retry may span a broken-then-recovered circuit), breaker inside it,
/// per-attempt timeout innermost so one hung request can never stack polls.
/// </summary>
public static class MarketDataResilience
{
    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        MarketDataOptions options,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        builder.TimeProvider = timeProvider;

        static bool IsRetryable(Outcome<HttpResponseMessage> outcome) =>
            outcome.Exception is HttpRequestException or TimeoutRejectedException
            || outcome.Result?.StatusCode is HttpStatusCode.RequestTimeout
                or >= HttpStatusCode.InternalServerError;

        static bool IsBreakerTrip(Outcome<HttpResponseMessage> outcome) =>
            IsRetryable(outcome) || outcome.Result?.StatusCode is HttpStatusCode.TooManyRequests;

        // 429 is deliberately excluded here (see IsRetryable vs IsBreakerTrip): retrying a
        // throttled endpoint at a 250ms base delay is amplification, not resilience — it
        // adds requests to the exact upstream that just asked us to slow down. The next
        // poll (PollInterval away) is the retry. Retry-After awareness (honouring the
        // upstream's own back-off hint) was deliberately not built: it would need
        // HttpRetryStrategyOptions instead of the plain RetryStrategyOptions used below, and
        // at a 60s poll cadence the next-poll-is-the-retry stance already makes an
        // in-pipeline retry moot — there is nothing a sub-poll-interval Retry-After would
        // buy here that the next poll doesn't already deliver.
        builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(250),
            ShouldHandle = args => ValueTask.FromResult(IsRetryable(args.Outcome))
        });

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            // Tuned so roughly two consecutive fully-retried failed polls open it: the
            // sampling window spans a few polls, and a 0.9 ratio over >=6 samples means
            // sustained failure, not one bad batch. 429 counts here even though it is not
            // retried (IsBreakerTrip vs IsRetryable, above): sustained throttling is exactly
            // the sustained-failure condition the breaker exists to detect and quiet down —
            // it just gets there in one attempt per poll instead of a retried one.
            FailureRatio = 0.9,
            MinimumThroughput = 6,
            SamplingDuration = TimeSpan.FromSeconds(90),
            BreakDuration = TimeSpan.FromMinutes(2),
            ShouldHandle = args => ValueTask.FromResult(IsBreakerTrip(args.Outcome)),

            // The spec's "once, not per poll" logging: state transitions announce
            // themselves here; the per-symbol path logs breaker fail-fasts at Debug.
            OnOpened = args =>
            {
                logger?.LogWarning(
                    "Market-data circuit opened for {Break} after sustained failures.",
                    args.BreakDuration);
                return ValueTask.CompletedTask;
            },
            OnClosed = _ =>
            {
                logger?.LogInformation("Market-data circuit closed; polls resume.");
                return ValueTask.CompletedTask;
            }
        });

        builder.AddTimeout(new TimeoutStrategyOptions { Timeout = options.AttemptTimeout });
    }
}
