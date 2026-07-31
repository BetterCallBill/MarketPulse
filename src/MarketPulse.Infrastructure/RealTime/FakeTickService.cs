using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketPulse.Infrastructure.RealTime;

public sealed class FakeTickService(
    PriceTickChannel channel,
    ILogger<FakeTickService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rng = new Random();
        var prices = SeedData.ReferenceTickers.ToDictionary(t => t.Code, t => t.SeedPrice);

        logger.LogInformation("FakeTickService started for {Count} tickers.", prices.Count);

        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var code in prices.Keys.ToArray())
                {
                    prices[code] = RandomWalk.Next(prices[code], rng);
                    await channel.Writer.WriteAsync(
                        new PriceTick(code, prices[code], DateTimeOffset.UtcNow),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("FakeTickService stopping.");
        }
    }
}
