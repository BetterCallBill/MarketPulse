namespace MarketPulse.Domain.Exceptions;

public abstract class DomainException(string message) : Exception(message)
{
    /// <summary>Stable slug used to build the ProblemDetails `type` URI.</summary>
    public abstract string ErrorCode { get; }

    /// <summary>
    /// HTTP status this failure maps to. Watchlist rule violations are conflicts, so 409
    /// is the default; the authentication exceptions override it.
    /// </summary>
    public virtual int StatusCode => 409;
}

public sealed class DuplicateTickerException(string ticker)
    : DomainException($"'{ticker}' is already on the watchlist.")
{
    public override string ErrorCode => "duplicate-ticker";
}

public sealed class WatchlistFullException(int max)
    : DomainException($"A watchlist may hold at most {max} items.")
{
    public override string ErrorCode => "watchlist-full";
}

public sealed class TickerNotOnWatchlistException(string ticker)
    : DomainException($"'{ticker}' is not on the watchlist.")
{
    public override string ErrorCode => "ticker-not-on-watchlist";
}
