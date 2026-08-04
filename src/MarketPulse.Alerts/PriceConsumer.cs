using System.Text.Json;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Alerts;

public sealed class PriceConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<PriceConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(publisherConfirms: false, stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, _options, stoppingToken);

        // Without a prefetch limit the broker pushes the whole queue at us and the TTL
        // stops protecting anything — the messages would already be in our process.
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, stoppingToken);

        await channel.BasicConsumeAsync(
            _options.PricesQueue, autoAck: false, consumer, stoppingToken);

        logger.LogInformation(
            "PriceConsumer listening on {Queue}.", _options.PricesQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });

        await channel.DisposeAsync();
    }

    private async Task HandleAsync(
        IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
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
