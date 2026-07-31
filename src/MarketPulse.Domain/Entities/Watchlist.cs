using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Domain.Entities;

public sealed class Watchlist
{
    public const int MaxItems = 20;

    private readonly List<WatchlistItem> _items = [];

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public IReadOnlyCollection<WatchlistItem> Items => _items.AsReadOnly();

    private Watchlist() { }

    public static Watchlist Create(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId
    };

    public void AddItem(string ticker)
    {
        var code = Normalise(ticker);

        if (_items.Count >= MaxItems)
        {
            throw new WatchlistFullException(MaxItems);
        }

        if (_items.Any(i => i.Ticker == code))
        {
            throw new DuplicateTickerException(code);
        }

        _items.Add(new WatchlistItem(Id, code));
    }

    public void RemoveItem(string ticker)
    {
        var code = Normalise(ticker);
        var item = _items.SingleOrDefault(i => i.Ticker == code)
            ?? throw new TickerNotOnWatchlistException(code);

        _items.Remove(item);
    }

    private static string Normalise(string ticker) => ticker.Trim().ToUpperInvariant();
}
