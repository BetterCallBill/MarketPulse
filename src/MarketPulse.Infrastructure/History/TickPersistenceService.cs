using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// Drains TickBuffer on a fixed clock and hands batches to the writer. The service is
/// effectively a singleton, so the scoped writer (and the DbContext under it) is resolved
/// from a fresh scope per flush — constructor injection here would be the classic captive
/// dependency. A failed flush drops that batch and logs: a hole in history is the accepted
/// cost of never letting persistence trouble reach the live stream.
/// </summary>
public sealed class TickPersistenceService(
    TickBuffer buffer,
    IServiceScopeFactory scopes,
    IOptions<HistoryOptions> options,
    TimeProvider timeProvider,
    ILogger<TickPersistenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.FlushIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await FlushAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Tick flush failed; that batch is lost, next interval retries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickPersistenceService stopping; flushing remaining ticks.");
        }

        // The stopping token is already cancelled; give the final batch its own bounded window.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await FlushAsync(shutdownCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Final tick flush failed; buffered ticks were lost.");
        }
    }

    /// <summary>
    /// Public seam, like OutboxDispatcher.DispatchPendingAsync: tests drive one flush
    /// directly instead of racing the timer. Drains the whole backlog in chunks of
    /// FlushBatchSize so no single INSERT exceeds the parameter budget.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var chunkSize = options.Value.FlushBatchSize;

        while (true)
        {
            var chunk = new List<PriceTick>(chunkSize);
            while (chunk.Count < chunkSize && buffer.Reader.TryRead(out var tick))
            {
                chunk.Add(tick);
            }

            if (chunk.Count == 0)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<IPriceTickBatchWriter>();
            await writer.WriteAsync(chunk, ct);

            if (chunk.Count < chunkSize)
            {
                return; // buffer drained
            }
        }
    }
}
