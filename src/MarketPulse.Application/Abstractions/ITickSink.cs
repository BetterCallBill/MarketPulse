using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Application.Abstractions;

/// <summary>
/// One destination for a price tick. Implementations are expected to be fast and are
/// allowed to fail: <c>TickBroadcaster</c> logs and continues rather than letting one sink
/// take down the others.
/// </summary>
public interface ITickSink
{
    Task SendAsync(PriceTick tick, CancellationToken ct);
}
