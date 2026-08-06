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
}
