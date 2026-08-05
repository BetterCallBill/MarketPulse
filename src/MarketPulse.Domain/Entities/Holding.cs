namespace MarketPulse.Domain.Entities;

/// <summary>
/// One ticker's position inside a portfolio. Mutated only by the aggregate — the internal
/// methods are the whole write surface, and the arithmetic lives here so the invariant
/// ("average cost and realised P&L are a pure function of the transaction stream") has one
/// home.
/// </summary>
public sealed class Holding
{
    public Guid Id { get; private set; }
    public Guid PortfolioId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public decimal Units { get; private set; }
    public decimal AverageCost { get; private set; }
    public decimal RealisedPnL { get; private set; }

    private Holding() { }

    internal Holding(Guid portfolioId, string ticker)
    {
        Id = Guid.NewGuid();
        PortfolioId = portfolioId;
        Ticker = ticker;
    }

    internal void ApplyBuy(decimal units, decimal price)
    {
        AverageCost = (Units * AverageCost + units * price) / (Units + units);
        Units += units;
    }

    internal void ApplySell(decimal units, decimal price)
    {
        RealisedPnL += units * (price - AverageCost);
        Units -= units;
        // AverageCost deliberately unchanged: a sell realises against it, never moves it.
        // A later buy from zero re-averages from (0 · avg) — the basis reset for free.
    }
}
