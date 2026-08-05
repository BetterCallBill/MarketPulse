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

    /// <summary>
    /// Set on every trade, deliberately load-bearing: a buy/sell otherwise only mutates a
    /// Holding row (a separate table via the owned-collection mapping), so nothing forces
    /// SaveChanges to touch the Portfolios row at all — and RowVersion is only ever checked
    /// on an UPDATE against that row. Writing this on every RecordBuy/RecordSell records the
    /// trade on the root, in the same unit of work.
    ///
    /// That alone is not sufficient — two trades can share a RecordedUtc (a fixed timestamp
    /// in a test, a batch import), which leaves the value looking unchanged to EF's
    /// snapshot-based tracking and the write would silently not happen. MarketPulseDbContext
    /// backstops this: it forces any Portfolio with a modified Holding into the Modified
    /// state before saving, independent of whether this property's value actually differs.
    /// Between the two, "the concurrency token lives on the portfolio row" holds
    /// unconditionally for every trade.
    /// </summary>
    public DateTimeOffset? LastTradedUtc { get; private set; }

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
