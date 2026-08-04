using MarketPulse.Api.Hubs;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.ValueObjects;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.RealTime;

/// <summary>
/// Slice 1's broadcast, unchanged in behaviour and moved behind the sink interface. Prices
/// are public data, so every authenticated client gets the same payload.
/// </summary>
public sealed class SignalRTickSink(IHubContext<PriceHub> hub) : ITickSink
{
    public Task SendAsync(PriceTick tick, CancellationToken ct) =>
        hub.Clients.All.SendAsync(
            "tick",
            new { ticker = tick.Ticker, price = tick.Price, timestampUtc = tick.TimestampUtc },
            ct);
}
