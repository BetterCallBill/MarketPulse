using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Infrastructure.History;

public interface IPriceTickBatchWriter
{
    /// <summary>Persists one batch. Replayed (Ticker, TimestampUtc) pairs are silently ignored.</summary>
    Task WriteAsync(IReadOnlyList<PriceTick> batch, CancellationToken ct);
}
