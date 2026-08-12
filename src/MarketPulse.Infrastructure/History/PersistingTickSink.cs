using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The ITickSink that only enqueues. Sinks run sequentially inside TickBroadcaster's loop,
/// so a database write here would tax the SignalR path 25 times a second in Fake mode.
/// </summary>
public sealed class PersistingTickSink(TickBuffer buffer) : ITickSink
{
    public Task SendAsync(PriceTick tick, CancellationToken ct)
    {
        // TryWrite on a DropOldest channel only fails once the channel is completed, which
        // never happens in normal operation; overflow is handled (and logged) by the
        // channel's own drop callback.
        buffer.Writer.TryWrite(tick);
        return Task.CompletedTask;
    }
}
