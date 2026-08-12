using System.Text.Json;
using MarketPulse.Api.Hubs;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Api.Messaging;

/// <summary>
/// Subscription, channel lifecycle and resubscription after a broker outage all live in
/// <see cref="RabbitMqConsumerService"/>. What is left here is the only part that is about
/// alerts: persist idempotently, push, ack — and classify a failure before acknowledging it.
/// </summary>
public sealed class AlertTriggeredConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationHub> hub,
    IOptions<RabbitMqOptions> options,
    ILogger<AlertTriggeredConsumer> logger)
    : RabbitMqConsumerService(connection, options, logger)
{
    protected override string QueueName => Options.NotificationsQueue;

    // A transient DbUpdateException republishes on this same channel (TransientRetry) before
    // acking the original. Without publisher confirmations that republish would return once
    // the bytes hit the socket, not once the broker accepted them — a false "succeeded" that
    // would let the original be acked while the copy was never actually durable. See ADR-009.
    protected override bool RequiresPublisherConfirms => true;

    protected override async Task HandleAsync(
        IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        using var activity = MessagingTelemetry.StartConsumerActivity(Options.NotificationsQueue, ea.BasicProperties);

        AlertTriggeredMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<AlertTriggeredMessage>(ea.Body.Span);
        }
        catch (JsonException ex)
        {
            // Permanent. Requeueing an unparseable message loops forever.
            logger.LogError(ex, "Unparseable alert message; dead-lettering.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        if (message is null)
        {
            logger.LogError("Null alert message; dead-lettering.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = ea.BasicProperties.CorrelationId,
            ["MessageId"] = message.MessageId
        });

        try
        {
            var notification = await PersistAsync(message, ct);

            if (notification is not null)
            {
                try
                {
                    await PushAsync(message, notification, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The row committed inside PersistAsync before we ever got here — that
                    // row, not this push, is the durability guarantee ("the row is the
                    // guarantee; real-time is the optimisation. If SignalR delivery fails or
                    // the user is offline, the row is already persisted and the panel will
                    // show it on next load"). A push failure must not undo an alert that
                    // already succeeded by dead-lettering it. Only this call is inside this
                    // catch — a PersistAsync failure must still reach the catches below.
                    logger.LogWarning(
                        ex, "Failed to push notification {NotificationId} over SignalR; " +
                            "the row is already persisted.", notification.Id);
                }
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (DbUpdateException ex) when (PermanentSqlError(ex) is { } number)
        {
            // The message itself is wrong, not the database. Requeueing it would replay the
            // same rejection at SQL-round-trip speed forever, hot-spinning the database and
            // growing the log without bound, and it would never reach the dead-letter queue
            // the spec's classification table puts it on.
            logger.LogError(
                ex, "Permanent data fault persisting a notification (SQL error {SqlError}); " +
                    "dead-lettering.", number);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
        catch (DbUpdateException ex)
        {
            // The database is unreachable or otherwise unhappy — the alert is real and the fault
            // is ours, so retry. Bounded since slice 8 (ADR-009's deferred work): republish with
            // an incremented x-retry-count, dead-letter at RetryLimit. Dead-lettering *before*
            // the cap would lose exactly what the outbox exists to protect.
            logger.LogWarning(ex, "Transient failure persisting a notification; retrying.");
            await TransientRetry.RetryOrDeadLetterAsync(
                channel, ea, Options.NotificationsQueue, Options.RetryLimit, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Permanent failure handling an alert; dead-lettering.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }

    /// <returns>The new notification, or null if this message was already handled.</returns>
    private async Task<Notification?> PersistAsync(
        AlertTriggeredMessage message, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var notification = Notification.Create(
            message.MessageId,
            message.UserId,
            message.AlertRuleId,
            message.Ticker,
            Enum.Parse<AlertDirection>(message.Direction),
            message.Threshold,
            message.TriggeredPrice,
            message.OccurredUtc,
            DateTimeOffset.UtcNow);

        await repo.AddAsync(notification, ct);

        try
        {
            await repo.SaveChangesAsync(ct);
            return notification;
        }
        catch (DbUpdateException ex) when (IsDuplicateMessageId(ex))
        {
            // Insert-and-catch, not read-then-write: the unique index on MessageId is the
            // dedupe, and a read-then-write would race itself under two API instances.
            logger.LogDebug("Duplicate delivery of {MessageId}; already stored.", message.MessageId);
            return null;
        }
    }

    private Task PushAsync(
        AlertTriggeredMessage message, Notification notification, CancellationToken ct) =>
        // The row is the guarantee; this push is the optimisation. A user who is offline
        // sees it on next load, which is why delivery failure here is not fatal.
        hub.Clients.User(message.UserId.ToString()).SendAsync(
            "notification",
            new
            {
                id = notification.Id,
                alertRuleId = message.AlertRuleId,
                ticker = message.Ticker,
                direction = message.Direction,
                threshold = message.Threshold,
                triggeredPrice = message.TriggeredPrice,
                occurredUtc = message.OccurredUtc
            },
            ct);

    /// <summary>
    /// SQL Server raises 2601 for a unique-index violation and 2627 for a unique-constraint
    /// one. Matching on the number rather than the message keeps this working under any
    /// server locale.
    /// </summary>
    private static bool IsDuplicateMessageId(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };

    /// <summary>
    /// SQL Server error numbers that say the <em>message</em> is unacceptable, as opposed to
    /// the database being unwell. Each one will be raised identically on every redelivery
    /// until someone changes the message or the schema, so retrying is pure cost:
    ///
    /// <list type="bullet">
    /// <item><description>515 — NULL into a non-nullable column.</description></item>
    /// <item><description>547 — foreign key or check constraint. This is the one the spec
    /// names explicitly: "a <c>UserId</c> that does not exist" belongs on the dead-letter
    /// path, and <c>Notifications.UserId</c> carries an FK to <c>Users</c>.</description></item>
    /// <item><description>245, 8114 — the value cannot be converted to the column's
    /// type.</description></item>
    /// <item><description>2628, 8152 — the value is too long for the column.</description></item>
    /// </list>
    ///
    /// <para>Deliberately an allow-list of permanent faults rather than a deny-list of
    /// transient ones, and deliberately keyed on the error number rather than the exception
    /// type — classifying on type is what let a foreign-key violation, a
    /// <c>DbUpdateException</c> like any other, requeue forever. Anything not listed falls
    /// through to requeue, because the asymmetry matters: requeueing a permanent fault wastes
    /// cycles, dead-lettering a transient one loses a user's alert.</para>
    /// </summary>
    /// <returns>The SQL error number if this fault is permanent, otherwise null.</returns>
    private static int? PermanentSqlError(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException
        {
            Number: 515 or 547 or 245 or 8114 or 2628 or 8152
        } sql
            ? sql.Number
            : null;
}
