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
            write.Users.Add(new User(userId, $"{userId}@test.local"));
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
            writeDb.Users.Add(new User(userId, $"{userId}@test.local"));

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
}
