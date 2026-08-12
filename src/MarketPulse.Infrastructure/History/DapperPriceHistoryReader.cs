using Dapper;
using MarketPulse.Application.Abstractions;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The codebase's first Dapper code — deliberately: these are read-heavy, shape-fixed
/// queries where materialising tracked entities buys nothing. Buckets are integer
/// arithmetic on seconds-since-2000, which the clustered (Ticker, TimestampUtc) index
/// feeds in order; DATEDIFF's int return holds until 2068.
/// </summary>
public sealed class DapperPriceHistoryReader(ISqlConnectionFactory connections) : IPriceHistoryReader
{
    private const string CandleSql = """
        WITH ranged AS (
            SELECT TimestampUtc, Price,
                   DATEDIFF(SECOND, '2000-01-01', TimestampUtc) / @IntervalSeconds AS Bucket
            FROM PriceTicks
            WHERE Ticker = @Ticker AND TimestampUtc >= @FromUtc AND TimestampUtc < @ToUtc
        ),
        ordered AS (
            SELECT Bucket, Price,
                   ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY TimestampUtc ASC)  AS RnOpen,
                   ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY TimestampUtc DESC) AS RnClose
            FROM ranged
        )
        SELECT
            TODATETIMEOFFSET(DATEADD(SECOND, Bucket * @IntervalSeconds, '2000-01-01'), 0) AS BucketStartUtc,
            MAX(CASE WHEN RnOpen  = 1 THEN Price END) AS [Open],
            MAX(Price)                                AS [High],
            MIN(Price)                                AS [Low],
            MAX(CASE WHEN RnClose = 1 THEN Price END) AS [Close]
        FROM ordered
        GROUP BY Bucket
        ORDER BY Bucket;
        """;

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string ticker, int intervalSeconds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Candle>(new CommandDefinition(
            CandleSql,
            new { Ticker = ticker, IntervalSeconds = intervalSeconds, FromUtc = fromUtc, ToUtc = toUtc },
            cancellationToken: ct));
        return rows.ToList();
    }

    private const string SparklineSql = """
        WITH ranged AS (
            SELECT Ticker, TimestampUtc, Price,
                   DATEDIFF(SECOND, '2000-01-01', TimestampUtc) / 60 AS Bucket
            FROM PriceTicks
            WHERE Ticker IN @Tickers AND TimestampUtc >= @FromUtc AND TimestampUtc < @ToUtc
        )
        SELECT Ticker, Bucket, Price AS [Close]
        FROM (
            SELECT Ticker, Bucket, Price,
                   ROW_NUMBER() OVER (PARTITION BY Ticker, Bucket ORDER BY TimestampUtc DESC) AS Rn
            FROM ranged
        ) x
        WHERE Rn = 1
        ORDER BY Ticker, Bucket;
        """;

    // DATEDIFF's SECOND return is int (holds until 2068, per the comment on CandleSql above),
    // so Bucket must be int here — Dapper's constructor-matching materialization requires an
    // exact type match against the column, and a long here throws at runtime, not compile time.
    private sealed record SparklinePoint(string Ticker, int Bucket, decimal Close);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<decimal>>> GetSparklinesAsync(
        IReadOnlyList<string> tickers, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (tickers.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<decimal>>();
        }

        await using var connection = await connections.OpenAsync(ct);
        var points = await connection.QueryAsync<SparklinePoint>(new CommandDefinition(
            SparklineSql,
            new { Tickers = tickers, FromUtc = fromUtc, ToUtc = toUtc },
            cancellationToken: ct));

        return points
            .GroupBy(p => p.Ticker, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<decimal>)g.Select(p => p.Close).ToList(),
                StringComparer.Ordinal);
    }
}
