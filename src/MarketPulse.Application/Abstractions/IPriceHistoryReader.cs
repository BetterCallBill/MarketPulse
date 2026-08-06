namespace MarketPulse.Application.Abstractions;

public record Candle(DateTimeOffset BucketStartUtc, decimal Open, decimal High, decimal Low, decimal Close);

public interface IPriceHistoryReader
{
    /// <summary>OHLC per bucket over [fromUtc, toUtc), oldest first. Buckets with no ticks are omitted.</summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string ticker, int intervalSeconds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
