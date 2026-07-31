using MarketPulse.Api.Hubs;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.RealTime;

public sealed class TickBroadcaster(
    PriceTickChannel channel,
    IHubContext<PriceHub> hub,
    ILogger<TickBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(stoppingToken))
            {
                await hub.Clients.All.SendAsync(
                    "tick",
                    new { ticker = tick.Ticker, price = tick.Price, timestampUtc = tick.TimestampUtc },
                    stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickBroadcaster stopping.");
        }
    }
}
