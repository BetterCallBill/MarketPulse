using MarketPulse.Infrastructure.History;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PriceTickTableTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Replayed_tick_is_ignored_not_an_error()
    {
        await using var db = fixture.CreateContext();
        const string insert =
            "INSERT INTO PriceTicks (Ticker, TimestampUtc, Price) " +
            "VALUES ('ZDUP', '2026-08-06T00:00:00+00:00', 10.5)";

        var first = await db.Database.ExecuteSqlRawAsync(insert);
        var second = await db.Database.ExecuteSqlRawAsync(insert); // replay: must not throw

        Assert.Equal(1, first);
        Assert.Equal(0, second); // IGNORE_DUP_KEY: silently dropped
        Assert.Equal(1, await db.PriceTicks.CountAsync(t => t.Ticker == "ZDUP"));
    }
}
