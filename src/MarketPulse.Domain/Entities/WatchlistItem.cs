namespace MarketPulse.Domain.Entities;

public sealed class WatchlistItem
{
    public Guid Id { get; private set; }
    public Guid WatchlistId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public DateTimeOffset AddedUtc { get; private set; }

    private WatchlistItem() { }

    internal WatchlistItem(Guid watchlistId, string ticker)
    {
        Id = Guid.NewGuid();
        WatchlistId = watchlistId;
        Ticker = ticker;
        AddedUtc = DateTimeOffset.UtcNow;
    }
}
