using MarketPulse.Infrastructure.History;
using MarketPulse.Infrastructure.Persistence;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class CandleQueryTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
    private DapperPriceHistoryReader _reader = null!;

    public async Task InitializeAsync()
    {
        _reader = new DapperPriceHistoryReader(new SqlConnectionFactory(fixture.ConnectionString));

        await using var db = fixture.CreateContext();
        if (await Task.FromResult(db.PriceTicks.Any(t => t.Ticker == "ZCND")))
        {
            return; // seeded by an earlier test in this class's lifetime
        }

        db.PriceTicks.AddRange(
            // bucket 10:00 — open 10, high 12, low 9, close 11
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(5), Price = 10m },
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(20), Price = 12m },
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(40), Price = 9m },
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(59), Price = 11m },
            // exactly on the boundary — belongs to bucket 10:01, alone: o=h=l=c
            new() { Ticker = "ZCND", TimestampUtc = T0.AddMinutes(1), Price = 13m });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task One_minute_candles_have_correct_ohlc_per_bucket()
    {
        var candles = await _reader.GetCandlesAsync(
            "ZCND", 60, T0, T0.AddMinutes(2), CancellationToken.None);

        Assert.Equal(2, candles.Count);

        Assert.Equal(T0, candles[0].BucketStartUtc);
        Assert.Equal(10m, candles[0].Open);
        Assert.Equal(12m, candles[0].High);
        Assert.Equal(9m, candles[0].Low);
        Assert.Equal(11m, candles[0].Close);

        Assert.Equal(T0.AddMinutes(1), candles[1].BucketStartUtc);
        Assert.Equal(13m, candles[1].Open);
        Assert.Equal(13m, candles[1].Close); // single tick: o=h=l=c
    }

    [Fact]
    public async Task The_to_bound_is_exclusive()
    {
        var candles = await _reader.GetCandlesAsync(
            "ZCND", 60, T0, T0.AddMinutes(1), CancellationToken.None);

        var only = Assert.Single(candles);
        Assert.Equal(T0, only.BucketStartUtc); // the 10:01:00 tick is outside [from, to)
    }

    [Fact]
    public async Task A_range_with_no_ticks_returns_empty()
    {
        var candles = await _reader.GetCandlesAsync(
            "ZCND", 60, T0.AddDays(1), T0.AddDays(1).AddMinutes(5), CancellationToken.None);

        Assert.Empty(candles);
    }
}
