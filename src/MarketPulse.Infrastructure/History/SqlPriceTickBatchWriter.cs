using System.Text;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// One parameterised multi-row INSERT per batch, executed through the DbContext's
/// connection. Not AddRange: change tracking buys nothing for append-only rows, and EF's
/// rows-affected propagation check would read IGNORE_DUP_KEY's silently-ignored duplicates
/// as a concurrency failure. Scoped — it follows the DbContext it writes through.
/// </summary>
public sealed class SqlPriceTickBatchWriter(MarketPulseDbContext db) : IPriceTickBatchWriter
{
    public async Task WriteAsync(IReadOnlyList<PriceTick> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // 3 parameters per row; HistoryOptions caps FlushBatchSize at 600, keeping every
        // command under SQL Server's 2,100-parameter limit.
        var sql = new StringBuilder("INSERT INTO PriceTicks (Ticker, TimestampUtc, Price) VALUES ");
        var parameters = new object[batch.Count * 3];

        for (var i = 0; i < batch.Count; i++)
        {
            sql.Append(i == 0 ? "" : ", ").Append($"(@t{i}, @ts{i}, @p{i})");
            parameters[i * 3] = new SqlParameter($"t{i}", batch[i].Ticker);
            parameters[i * 3 + 1] = new SqlParameter($"ts{i}", batch[i].TimestampUtc);
            parameters[i * 3 + 2] = new SqlParameter($"p{i}", batch[i].Price);
        }

        await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, ct);
    }
}
