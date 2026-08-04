using MarketPulse.Application.Configuration;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Every exchange, queue and binding in the system, declared in one place. Both hosts call
/// this at startup; AMQP declarations are idempotent, so whichever boots first wins and the
/// other is a no-op. Neither process has to wait for the other.
/// </summary>
public static class RabbitMqTopology
{
    public const string AlertTriggeredRoutingKey = "alert.triggered";
    public const string PriceTickRoutingKeyPrefix = "price.tick";

    /// <summary>
    /// The ticker rides in the routing key so a future worker can bind to a subset rather
    /// than filtering everything in process.
    /// </summary>
    public static string RoutingKeyFor(string ticker) =>
        $"{PriceTickRoutingKeyPrefix}.{ticker.Trim().ToUpperInvariant()}";

    public static async Task DeclareAsync(
        IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        // --- Prices: lossy on purpose ------------------------------------------------
        await channel.ExchangeDeclareAsync(
            options.PricesExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: ct);

        await channel.QueueDeclareAsync(
            options.PricesQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                // Stale prices are worse than no prices: they would fire alerts against a
                // market that has already moved on.
                ["x-message-ttl"] = options.TickTtlMilliseconds,
                ["x-max-length"] = options.TickQueueMaxLength,
                ["x-overflow"] = "drop-head"
            },
            cancellationToken: ct);

        await channel.QueueBindAsync(
            options.PricesQueue, options.PricesExchange,
            $"{PriceTickRoutingKeyPrefix}.#", cancellationToken: ct);

        // --- Alerts: durable, confirmed, dead-lettered --------------------------------
        await channel.ExchangeDeclareAsync(
            options.AlertsExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            options.AlertsDeadLetterExchange, ExchangeType.Fanout, durable: true,
            autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            options.NotificationsQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = options.AlertsDeadLetterExchange
            },
            cancellationToken: ct);

        await channel.QueueBindAsync(
            options.NotificationsQueue, options.AlertsExchange, AlertTriggeredRoutingKey,
            cancellationToken: ct);

        await channel.QueueDeclareAsync(
            options.NotificationsDeadLetterQueue,
            durable: true, exclusive: false, autoDelete: false, arguments: null,
            cancellationToken: ct);

        // Terminal. Nothing consumes this queue; it is drained by hand when something
        // has gone wrong, which is the point of a dead-letter queue.
        await channel.QueueBindAsync(
            options.NotificationsDeadLetterQueue, options.AlertsDeadLetterExchange,
            routingKey: string.Empty, cancellationToken: ct);
    }
}
