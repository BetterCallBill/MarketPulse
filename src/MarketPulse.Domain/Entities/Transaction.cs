namespace MarketPulse.Domain.Entities;

public enum TransactionSide { Buy, Sell }

/// <summary>
/// One recorded trade. Append-only by construction: the constructor is internal so only
/// the aggregate mints one, no mutators exist, and the API exposes no delete — an
/// incorrect trade is corrected by an offsetting one (see ADR-004's consequences).
/// </summary>
public sealed class Transaction
{
    public Guid Id { get; private set; }
    public Guid PortfolioId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public TransactionSide Side { get; private set; }
    public decimal Units { get; private set; }
    public decimal Price { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public DateTimeOffset RecordedUtc { get; private set; }

    private Transaction() { }

    internal Transaction(
        Guid portfolioId, string ticker, TransactionSide side,
        decimal units, decimal price, DateTimeOffset occurredUtc, DateTimeOffset recordedUtc)
    {
        Id = Guid.NewGuid();
        PortfolioId = portfolioId;
        Ticker = ticker;
        Side = side;
        Units = units;
        Price = price;
        OccurredUtc = occurredUtc;
        RecordedUtc = recordedUtc;
    }
}
