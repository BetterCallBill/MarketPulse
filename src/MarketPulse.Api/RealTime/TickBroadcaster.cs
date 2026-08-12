using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.RealTime;

namespace MarketPulse.Api.RealTime;

/// <summary>
/// The single reader of <see cref="PriceTickChannel"/>. It has to be single: a Channel&lt;T&gt;
/// distributes items among its readers rather than broadcasting them, so a second hosted
/// service reading the same channel would silently steal half the ticks. Fan-out happens
/// here, over sinks.
/// </summary>
public sealed class TickBroadcaster(
    PriceTickChannel channel,
    IEnumerable<ITickSink> sinks,
    ILogger<TickBroadcaster> logger) : BackgroundService
{
    private readonly ITickSink[] _sinks = sinks.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(stoppingToken))
            {
                foreach (var sink in _sinks)
                {
                    try
                    {
                        await sink.SendAsync(tick, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One sink failing must not stop the others, and must not stop the
                        // reader — a dead reader means the channel fills and drops ticks
                        // for everyone.
                        logger.LogWarning(
                            ex, "Tick sink {Sink} failed for {Ticker}.",
                            sink.GetType().Name, tick.Ticker);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickBroadcaster stopping.");
        }
    }
}
