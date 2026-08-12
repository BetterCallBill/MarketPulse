using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class WatchlistPersistenceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Reference_tickers_are_seeded()
    {
        await using var db = fixture.CreateContext();

        Assert.Equal(25, await db.Tickers.CountAsync());
    }

    [Fact]
    public async Task Watchlist_round_trips_with_its_items()
    {
        var userId = Guid.NewGuid();

        await using (var write = fixture.CreateContext())
        {
            write.Users.Add(new User(userId, $"{userId}@test.local", "hash", DateTimeOffset.UtcNow));
            var watchlist = Watchlist.Create(userId);
            watchlist.AddItem("IVV");
            watchlist.AddItem("NDQ");
            await write.Watchlists.AddAsync(watchlist);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var loaded = await read.Watchlists.SingleAsync(w => w.UserId == userId);

        Assert.Equal(2, loaded.Items.Count);
        Assert.Contains(loaded.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public async Task Repository_round_trips_watchlist_and_checks_ticker_existence()
    {
        var userId = Guid.NewGuid();

        await using (var writeDb = fixture.CreateContext())
        {
            writeDb.Users.Add(new User(userId, $"{userId}@test.local", "hash", DateTimeOffset.UtcNow));

            var writeRepo = new WatchlistRepository(writeDb);
            var watchlist = Watchlist.Create(userId);
            watchlist.AddItem("IVV");
            watchlist.AddItem("NDQ");
            await writeRepo.AddAsync(watchlist, CancellationToken.None);
            await writeRepo.SaveChangesAsync(CancellationToken.None);
        }

        await using var readDb = fixture.CreateContext();
        var readRepo = new WatchlistRepository(readDb);

        var loaded = await readRepo.GetForUserAsync(userId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Items.Count);
        Assert.Contains(loaded.Items, i => i.Ticker == "IVV");

        Assert.True(await readRepo.TickerExistsAsync("IVV", CancellationToken.None));
        Assert.False(await readRepo.TickerExistsAsync("NOPE", CancellationToken.None));
    }

    /// <summary>
    /// Adding to a watchlist that was *loaded from the database* is a different EF code path
    /// from adding to one being created: the principal is Unchanged rather than Added, so the
    /// new item is classified on its own. Because the Domain mints the item's id in its
    /// constructor, EF's default convention read that set key as "this row already exists"
    /// and emitted an UPDATE matching no row, throwing DbUpdateConcurrencyException.
    ///
    /// Every slice 1 test happened to take the create path, so this was uncovered. The API
    /// tests only guard it incidentally, via a registration that provisions a watchlist
    /// eagerly; this pins it at the persistence layer where the behaviour actually lives.
    /// </summary>
    [Fact]
    public async Task An_item_added_to_a_watchlist_loaded_from_the_database_is_inserted()
    {
        var userId = Guid.NewGuid();
        Guid watchlistId;

        await using (var seedDb = fixture.CreateContext())
        {
            seedDb.Users.Add(new User(userId, $"{userId}@test.local", "hash", DateTimeOffset.UtcNow));

            var watchlist = Watchlist.Create(userId);
            watchlistId = watchlist.Id;
            await seedDb.Watchlists.AddAsync(watchlist);
            await seedDb.SaveChangesAsync();
        }

        // Second context: the aggregate is now loaded and persisted, not newly created.
        // A regression here throws DbUpdateConcurrencyException out of SaveChangesAsync.
        await using (var writeDb = fixture.CreateContext())
        {
            var loaded = await writeDb.Watchlists.SingleAsync(w => w.Id == watchlistId);
            Assert.Empty(loaded.Items);

            loaded.AddItem("IVV");
            await writeDb.SaveChangesAsync();
        }

        // Third context, so the assertion reads real rows rather than a tracked graph:
        // an UPDATE that silently affected nothing would leave this empty.
        await using var readDb = fixture.CreateContext();
        var reloaded = await readDb.Watchlists.SingleAsync(w => w.Id == watchlistId);

        Assert.Single(reloaded.Items);
        Assert.Contains(reloaded.Items, i => i.Ticker == "IVV");
    }
}
