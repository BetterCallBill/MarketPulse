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

public sealed class AlertTriggeredConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationHub> hub,
    IOptions<RabbitMqOptions> options,
    ILogger<AlertTriggeredConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(publisherConfirms: false, stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, _options, stoppingToken);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, stoppingToken);

        await channel.BasicConsumeAsync(
            _options.NotificationsQueue, autoAck: false, consumer, stoppingToken);

        logger.LogInformation(
            "AlertTriggeredConsumer listening on {Queue}.", _options.NotificationsQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });

        await channel.DisposeAsync();
    }

    private async Task HandleAsync(
        IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
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
                await PushAsync(message, notification, ct);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (DbUpdateException ex)
        {
            // The database is unreachable or otherwise unhappy — the alert is real and the
            // fault is ours, so requeue. Dead-lettering here would lose exactly what the
            // outbox exists to protect. This can loop while SQL Server is down; that is
            // deliberate, and the redelivery counter that would bound it is named in the
            // spec as observability-slice work.
            logger.LogWarning(ex, "Transient failure persisting a notification; requeueing.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, ct);
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
}
