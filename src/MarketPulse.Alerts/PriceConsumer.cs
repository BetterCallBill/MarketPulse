using System.Text.Json;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Alerts;

/// <summary>
/// Subscription, channel lifecycle and resubscription after a broker outage all live in
/// <see cref="RabbitMqConsumerService"/>. What is left here is the only part that is about
/// prices: deserialize a tick, evaluate it, ack.
/// </summary>
public sealed class PriceConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<PriceConsumer> logger) : RabbitMqConsumerService(connection, options, logger)
{
    protected override string QueueName => Options.PricesQueue;

    protected override async Task HandleAsync(
        IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        using var activity = MessagingTelemetry.StartConsumerActivity(Options.PricesQueue, ea.BasicProperties);

        var correlationId = ea.BasicProperties.CorrelationId;

        try
        {
            var tick = JsonSerializer.Deserialize<PriceTickMessage>(ea.Body.Span);

            if (tick is null)
            {
                logger.LogWarning("Discarding an unreadable tick.");
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            using var logScope = logger.BeginScope(
                new Dictionary<string, object?> { ["CorrelationId"] = correlationId });

            await scope.ServiceProvider
                .GetRequiredService<AlertEvaluator>()
                .EvaluateAsync(tick, correlationId, ct);

            // Acked only after the transaction commits. Dying in between redelivers the
            // tick, and the rule is no longer Active, so nothing happens twice.
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to evaluate a tick; discarding it.");

            // Ticks are lossy by design and there is no dead-letter queue on the price
            // path: another tick for this ticker arrives in one second.
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }
}
