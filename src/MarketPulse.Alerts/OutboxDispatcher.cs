using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Alerts;

/// <summary>
/// Relays pending outbox rows to the broker. Polls rather than listens: a dispatcher that
/// missed a notification signal would strand the row until something else happened to wake
/// it, whereas a poll loop is self-healing by construction. Half a second of latency on a
/// path that is already eventually consistent is not worth engineering away.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await DispatchPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The broker or the database is unwell. Rows stay pending and the next
                    // tick tries again — this loop must never be the thing that stops.
                    logger.LogWarning(ex, "Outbox dispatch pass failed; retrying.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("OutboxDispatcher stopping.");
        }
    }

    /// <returns>How many rows were confirmed and marked dispatched.</returns>
    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketPulseDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var pending = await db.OutboxMessages
            .Where(m => m.DispatchedUtc == null)
            .OrderBy(m => m.OccurredUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        var dispatched = 0;

        foreach (var message in pending)
        {
            try
            {
                await publisher.PublishAsync(
                    RabbitMqTopology.AlertTriggeredRoutingKey,
                    message.Id,
                    message.Payload,
                    message.CorrelationId,
                    ct);

                // Only now. A publish that was never confirmed must leave this row alone
                // so the next pass retries it — at-least-once, which is why the consumer
                // dedupes on message id.
                message.MarkDispatched(DateTimeOffset.UtcNow);
                dispatched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.RecordAttempt();
                logger.LogWarning(
                    ex, "Publishing outbox message {MessageId} failed (attempt {Attempt}).",
                    message.Id, message.AttemptCount);
            }
        }

        await db.SaveChangesAsync(ct);
        return dispatched;
    }
}
