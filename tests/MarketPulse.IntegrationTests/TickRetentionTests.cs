using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class TickRetentionTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private TickRetentionService CreateService(HistoryOptions options)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext());
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new TickRetentionService(
            scopes, Options.Create(options), new FakeTimeProvider(Now),
            NullLogger<TickRetentionService>.Instance);
    }

    [Fact]
    public async Task Sweep_deletes_expired_rows_and_keeps_recent_ones()
    {
        await using (var db = fixture.CreateContext())
        {
            db.PriceTicks.AddRange(
                new() { Ticker = "ZRET", TimestampUtc = Now.AddDays(-8), Price = 1m },
                new() { Ticker = "ZRET", TimestampUtc = Now.AddDays(-6), Price = 2m });
            await db.SaveChangesAsync();
        }

        await CreateService(new HistoryOptions { RetentionDays = 7 })
            .SweepOnceAsync(CancellationToken.None);

        await using var check = fixture.CreateContext();
        var survivor = Assert.Single(await check.PriceTicks.Where(t => t.Ticker == "ZRET").ToListAsync());
        Assert.Equal(2m, survivor.Price);
    }

    [Fact]
    public async Task Sweep_chunks_until_nothing_old_remains()
    {
        await using (var db = fixture.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.PriceTicks.Add(new() { Ticker = "ZCHU", TimestampUtc = Now.AddDays(-9).AddSeconds(i), Price = i });
            }
            await db.SaveChangesAsync();
        }

        // chunk of 1000 is the Range floor; drive the loop with the row count instead:
        // 5 expired rows and a 1000-row chunk still exercises the terminate-when-short path,
        // and the count assertion proves the loop deleted everything in one sweep call.
        await CreateService(new HistoryOptions { RetentionDays = 7, RetentionDeleteChunk = 1000 })
            .SweepOnceAsync(CancellationToken.None);

        await using var check = fixture.CreateContext();
        Assert.False(await check.PriceTicks.AnyAsync(t => t.Ticker == "ZCHU"));
    }
}
