using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Domain.Entities;

/// <summary>
/// One per user, created implicitly on the first transaction. Holdings are bounded (one per
/// ticker) and live inside the aggregate; transactions are unbounded, so the aggregate mints
/// them (internal constructor — nothing else can) but does not hold them: the repository
/// persists what RecordBuy/RecordSell return, and reads page the table directly. That split
/// is the CQRS scope ADR-004 documents.
/// </summary>
public sealed class Portfolio
{
    private readonly List<Holding> _holdings = [];

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public IReadOnlyCollection<Holding> Holdings => _holdings.AsReadOnly();

    /// <summary>
    /// Two concurrent sells of the same holding must not oversell. The loser's UPDATE
    /// matches no row; the API answers 409 and the client retries against fresh state.
    /// The anomaly test pair demonstrates exactly this with the token bypassed and not.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>When the user last traded, for display. Not load-bearing for concurrency —
    /// see <see cref="Version"/> for that.</summary>
    public DateTimeOffset? LastTradedUtc { get; private set; }

    /// <summary>
    /// A monotonic counter on the root, bumped on every trade. A buy/sell otherwise only
    /// mutates a Holding row — a separate table via the owned-collection mapping — so
    /// nothing would otherwise force SaveChanges to touch the Portfolios row at all, and
    /// RowVersion (the actual concurrency token) is only ever checked on an UPDATE against
    /// that row. Incrementing this on every trade guarantees a scalar on the root always
    /// changes, in the same unit of work as the trade, which is what puts the portfolio row
    /// — and its RowVersion check — into every trade's UPDATE. Deliberately load-bearing and
    /// entirely visible here in the Domain, not behind persistence-layer plumbing: any future
    /// method that mutates a Holding must bump this too, or its writes go unguarded exactly
    /// as this one did before Version existed.
    /// </summary>
    public long Version { get; private set; }

    private Portfolio() { }

    public static Portfolio Create(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId
    };

    public Transaction RecordBuy(
        string ticker, decimal units, decimal price,
        DateTimeOffset occurredUtc, DateTimeOffset recordedUtc)
    {
        var code = Validate(ticker, units, price);

        var holding = _holdings.FirstOrDefault(h => h.Ticker == code);
        if (holding is null)
        {
            holding = new Holding(Id, code);
            _holdings.Add(holding);
        }

        holding.ApplyBuy(units, price);
        LastTradedUtc = recordedUtc;
        Version++;
        return new Transaction(Id, code, TransactionSide.Buy, units, price, occurredUtc, recordedUtc);
    }

    public Transaction RecordSell(
        string ticker, decimal units, decimal price,
        DateTimeOffset occurredUtc, DateTimeOffset recordedUtc)
    {
        var code = Validate(ticker, units, price);

        var holding = _holdings.FirstOrDefault(h => h.Ticker == code);
        var held = holding?.Units ?? 0m;
        if (holding is null || held < units)
        {
            throw new InsufficientHoldingsException(held, units);
        }

        holding.ApplySell(units, price);
        LastTradedUtc = recordedUtc;
        Version++;
        return new Transaction(Id, code, TransactionSide.Sell, units, price, occurredUtc, recordedUtc);
    }

    private static string Validate(string ticker, decimal units, decimal price)
    {
        if (units <= 0)
        {
            throw new InvalidTradeException("Units must be greater than zero.");
        }

        if (price <= 0)
        {
            throw new InvalidTradeException("Price must be greater than zero.");
        }

        return ticker.Trim().ToUpperInvariant();
    }
}
