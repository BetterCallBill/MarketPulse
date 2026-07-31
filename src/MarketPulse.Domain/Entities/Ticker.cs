namespace MarketPulse.Domain.Entities;

/// <summary>Reference data: the set of ASX ETF codes a watchlist item may name.</summary>
public sealed class Ticker
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public decimal SeedPrice { get; private set; }

    private Ticker() { }

    public Ticker(string code, string name, decimal seedPrice)
    {
        Code = code;
        Name = name;
        SeedPrice = seedPrice;
    }
}
