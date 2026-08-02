namespace MarketPulse.Domain.Entities;

public sealed class WatchlistItem
{
    public Guid Id { get; private set; }
    public Guid WatchlistId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public DateTimeOffset AddedUtc { get; private set; }

    private WatchlistItem() { }

    /// <summary>
    /// `Id` is deliberately left unset for EF to generate. Assigning it here makes the key
    /// "set" at the moment the item is attached to an already-persisted watchlist, and EF's
    /// graph tracking reads a set key as "this row exists" — it emits an UPDATE that matches
    /// no row instead of an INSERT. The bug only surfaces on a watchlist that was loaded
    /// from the database, which is every add after the very first one.
    /// </summary>
    internal WatchlistItem(Guid watchlistId, string ticker)
    {
        WatchlistId = watchlistId;
        Ticker = ticker;
        AddedUtc = DateTimeOffset.UtcNow;
    }
}
