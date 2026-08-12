namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The persistence row for one observed tick. Infrastructure-only: it exists so EF can
/// model and migrate the table (and tests can query it); the write path itself uses raw
/// SQL, and Domain's PriceTick value object is deliberately unrelated.
/// </summary>
public sealed class PriceTickRow
{
    public required string Ticker { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required decimal Price { get; init; }
}
