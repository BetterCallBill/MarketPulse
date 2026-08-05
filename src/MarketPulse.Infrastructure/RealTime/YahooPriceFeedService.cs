using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The real tick producer: one keyless chart request per seeded symbol per poll, written
/// to the same channel the fake fills. Failure semantics are 4a's, unchanged: ticks are
/// lossy by design — a failed symbol is skipped with one warning, a failed poll writes
/// nothing, the next poll self-heals, and when the upstream is truly down the breaker
/// (composed in DI) makes the failures cheap while the dashboard's staleness UI tells the
/// truth. There is deliberately no fallback to the fake: invented prices presented as real
/// ones is the one failure mode this slice refuses (ADR-010).
/// </summary>
public sealed class YahooPriceFeedService(
    YahooQuoteClient client,
    PriceTickChannel channel,
    IOptions<MarketDataOptions> options,
    ILogger<YahooPriceFeedService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var codes = SeedData.ReferenceTickers.Select(t => t.Code).ToArray();
        logger.LogInformation(
            "YahooPriceFeedService polling {Count} symbols every {Interval}.",
            codes.Length, options.Value.PollInterval);

        using var timer = new PeriodicTimer(options.Value.PollInterval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PollOnceAsync(codes, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("YahooPriceFeedService stopping.");
        }
    }

    private async Task PollOnceAsync(string[] codes, CancellationToken ct)
    {
        // Concurrent within the poll, bounded by the symbol count (~8) — no throttling
        // machinery for a workload this small.
        var polls = codes.Select(async code =>
        {
            try
            {
                var price = await client.GetPriceAsync(YahooSymbols.ToYahoo(code), ct);

                if (price is not { } value)
                {
                    logger.LogWarning("No usable quote for {Code}; skipping this poll.", code);
                    return;
                }

                await channel.Writer.WriteAsync(
                    new PriceTick(code, value, timeProvider.GetUtcNow()), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not failure.
            }
            catch (Polly.CircuitBreaker.BrokenCircuitException)
            {
                // The breaker already announced itself once at Warning (OnOpened); eight
                // symbols repeating it every poll would be noise. Debug keeps the trace.
                logger.LogDebug("Circuit open; skipped {Code} this poll.", code);
            }
            catch (Exception ex)
            {
                // Transport failure past the pipeline. One warning; the symbol misses one
                // poll. Lossy by design.
                logger.LogWarning(ex, "Quote request failed for {Code}; skipping this poll.", code);
            }
        });

        await Task.WhenAll(polls);
    }
}
