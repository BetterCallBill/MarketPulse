using MarketPulse.Domain.Entities;

namespace MarketPulse.Infrastructure.Persistence;

public static class SeedData
{
    public static readonly Guid DevUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public const string DevUserEmail = "dev@marketpulse.local";

    /// <summary>Fixed id for the dev user's seeded watchlist. `HasData` requires a stable key.</summary>
    public static readonly Guid DevWatchlistId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>
    /// Fixed timestamp for the dev user's seeded watchlist items. `HasData` requires
    /// deterministic values — `DateTimeOffset.UtcNow` would produce a new migration diff
    /// on every run.
    /// </summary>
    public static readonly DateTimeOffset DevWatchlistItemsAddedUtc =
        new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Seeded on the dev user's watchlist at first run.</summary>
    public static readonly string[] DefaultWatchlist = ["IVV", "NDQ", "VHY", "FANG"];

    /// <summary>Fixed ids for the seeded watchlist items, positionally paired with <see cref="DefaultWatchlist"/>.</summary>
    public static readonly IReadOnlyList<Guid> DefaultWatchlistItemIds =
    [
        Guid.Parse("33333333-3333-3333-3333-333333333001"),
        Guid.Parse("33333333-3333-3333-3333-333333333002"),
        Guid.Parse("33333333-3333-3333-3333-333333333003"),
        Guid.Parse("33333333-3333-3333-3333-333333333004")
    ];

    public static readonly IReadOnlyList<Ticker> ReferenceTickers =
    [
        new("IVV",  "iShares S&P 500 ETF",                    62.10m),
        new("NDQ",  "BetaShares NASDAQ 100 ETF",              54.30m),
        new("VHY",  "Vanguard Australian Shares High Yield",  68.75m),
        new("FANG", "Global X FANG+ ETF",                     26.40m),
        new("VAS",  "Vanguard Australian Shares Index",       98.20m),
        new("A200", "BetaShares Australia 200 ETF",          142.60m),
        new("VGS",  "Vanguard MSCI Intl Shares Index",       125.90m),
        new("IOZ",  "iShares Core S&P/ASX 200 ETF",           34.85m),
        new("STW",  "SPDR S&P/ASX 200 Fund",                  74.90m),
        new("VAP",  "Vanguard Australian Property Securities",88.40m),
        new("VAF",  "Vanguard Australian Fixed Interest",     45.15m),
        new("IOO",  "iShares Global 100 ETF",                140.25m),
        new("QUAL", "VanEck MSCI Intl Quality ETF",           48.70m),
        new("ETHI", "BetaShares Global Sustainability",       14.55m),
        new("HACK", "BetaShares Global Cybersecurity ETF",    12.30m),
        new("ACDC", "Global X Battery Tech & Lithium ETF",    72.15m),
        new("ASIA", "BetaShares Asia Technology Tigers ETF",  11.85m),
        new("GEAR", "BetaShares Geared Australian Equity",    32.40m),
        new("MOAT", "VanEck Morningstar Wide Moat ETF",      118.90m),
        new("IHVV", "iShares S&P 500 AUD Hedged ETF",         46.55m),
        new("VTS",  "Vanguard US Total Market Shares",       410.30m),
        new("VEU",  "Vanguard All-World ex-US Shares",        94.20m),
        new("SLF",  "SPDR S&P/ASX 200 Listed Property",       13.75m),
        new("SYI",  "SPDR MSCI Australia Select High Div",    29.60m),
        new("RBTZ", "Global X ROBO Global Robotics ETF",      21.05m)
    ];
}
