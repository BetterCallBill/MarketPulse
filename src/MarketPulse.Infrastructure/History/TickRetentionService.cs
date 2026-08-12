using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// Keeps PriceTicks bounded: rows older than RetentionDays are deleted on an hourly clock,
/// in chunks, so no single DELETE holds locks long enough to matter. The debt RefreshTokens
/// still owes (INTERVIEW-QA Q8.5), not repeated here.
/// </summary>
public sealed class TickRetentionService(
    IServiceScopeFactory scopes,
    IOptions<HistoryOptions> options,
    TimeProvider timeProvider,
    ILogger<TickRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.RetentionSweepIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retention sweep failed; next interval retries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickRetentionService stopping.");
        }
    }

    /// <summary>Public seam for tests, like OutboxDispatcher.DispatchPendingAsync.</summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.RetentionDays);
        var chunk = options.Value.RetentionDeleteChunk;
        var total = 0;
        int deleted;

        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketPulseDbContext>();
            deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({chunk}) FROM PriceTicks WHERE TimestampUtc < {cutoff}", ct);
            total += deleted;
        }
        while (deleted == chunk);

        if (total > 0)
        {
            logger.LogInformation(
                "Retention sweep deleted {Count} ticks older than {Cutoff:u}.", total, cutoff);
        }
    }
}
