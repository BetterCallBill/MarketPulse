namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The seed tickers are bare ASX codes; Yahoo speaks exchange-suffixed symbols. One suffix,
/// two directions, no table — every seeded instrument is ASX by product definition.
/// </summary>
public static class YahooSymbols
{
    private const string AsxSuffix = ".AX";

    public static string ToYahoo(string asxCode) => $"{asxCode}{AsxSuffix}";

    public static string? ToAsx(string yahooSymbol) =>
        yahooSymbol.EndsWith(AsxSuffix, StringComparison.Ordinal)
            ? yahooSymbol[..^AsxSuffix.Length]
            : null;
}
