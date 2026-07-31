using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.UnitTests.Domain;

public class WatchlistTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void AddItem_adds_the_ticker()
    {
        var watchlist = Watchlist.Create(UserId);

        watchlist.AddItem("IVV");

        Assert.Contains(watchlist.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public void AddItem_normalises_ticker_to_uppercase()
    {
        var watchlist = Watchlist.Create(UserId);

        watchlist.AddItem("ivv");

        Assert.Contains(watchlist.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public void AddItem_rejects_a_duplicate_ticker()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");

        Assert.Throws<DuplicateTickerException>(() => watchlist.AddItem("IVV"));
    }

    [Fact]
    public void AddItem_rejects_a_duplicate_regardless_of_casing()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");

        Assert.Throws<DuplicateTickerException>(() => watchlist.AddItem("ivv"));
    }

    [Fact]
    public void AddItem_rejects_the_twenty_first_item()
    {
        var watchlist = Watchlist.Create(UserId);
        for (var i = 0; i < 20; i++)
        {
            watchlist.AddItem($"TK{i:D2}");
        }

        Assert.Throws<WatchlistFullException>(() => watchlist.AddItem("EXTRA"));
    }

    [Fact]
    public void AddItem_reports_full_not_duplicate_when_at_capacity()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");
        for (var i = 1; i < 20; i++)
        {
            watchlist.AddItem($"TK{i:D2}");
        }

        // With 20 items, adding a duplicate should throw WatchlistFullException
        // because capacity is checked before duplicate uniqueness
        Assert.Throws<WatchlistFullException>(() => watchlist.AddItem("IVV"));
    }

    [Fact]
    public void RemoveItem_removes_the_ticker()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");

        watchlist.RemoveItem("IVV");

        Assert.Empty(watchlist.Items);
    }

    [Fact]
    public void RemoveItem_rejects_a_ticker_that_is_not_present()
    {
        var watchlist = Watchlist.Create(UserId);

        Assert.Throws<TickerNotOnWatchlistException>(() => watchlist.RemoveItem("IVV"));
    }
}
