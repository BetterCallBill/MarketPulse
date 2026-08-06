using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.History;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PriceTickBatchWriterTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Writes_a_batch_in_one_round_trip()
    {
        await using var db = fixture.CreateContext();
        var writer = new SqlPriceTickBatchWriter(db);

        await writer.WriteAsync(
        [
            new PriceTick("ZBW1", 10m, T0),
            new PriceTick("ZBW1", 11m, T0.AddSeconds(1)),
            new PriceTick("ZBW2", 20m, T0)
        ], CancellationToken.None);

        Assert.Equal(2, await db.PriceTicks.CountAsync(t => t.Ticker == "ZBW1"));
        Assert.Equal(1, await db.PriceTicks.CountAsync(t => t.Ticker == "ZBW2"));
    }

    [Fact]
    public async Task A_replayed_tick_inside_a_batch_does_not_fail_the_batch()
    {
        await using var db = fixture.CreateContext();
        var writer = new SqlPriceTickBatchWriter(db);
        await writer.WriteAsync([new PriceTick("ZBW3", 10m, T0)], CancellationToken.None);

        // one duplicate, one genuinely new — the new one must land
        await writer.WriteAsync(
        [
            new PriceTick("ZBW3", 10m, T0),
            new PriceTick("ZBW3", 12m, T0.AddSeconds(5))
        ], CancellationToken.None);

        Assert.Equal(2, await db.PriceTicks.CountAsync(t => t.Ticker == "ZBW3"));
    }

    [Fact]
    public async Task An_empty_batch_is_a_no_op()
    {
        await using var db = fixture.CreateContext();
        var writer = new SqlPriceTickBatchWriter(db);

        await writer.WriteAsync([], CancellationToken.None); // must not throw
    }
}
