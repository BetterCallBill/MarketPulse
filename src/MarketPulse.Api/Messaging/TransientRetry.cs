using System.Text;
using MarketPulse.Application.Telemetry;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Api.Messaging;

/// <summary>
/// The bound ADR-009 deferred to this slice. A transient failure republishes the message
/// to the same queue with an incremented x-retry-count and acks the original; at the cap
/// it dead-letters. Republishing (rather than nack+requeue) is what makes the counter
/// possible — a requeued message arrives with identical headers, so nothing can count it.
/// The retries stay hot (broker-speed); the cap is what turns "forever" into "≤ RetryLimit".
/// </summary>
public static class TransientRetry
{
    public const string RetryCountHeader = "x-retry-count";

    public static int ReadRetryCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is { } headers && headers.TryGetValue(RetryCountHeader, out var raw))
        {
            return raw switch
            {
                int i => i,
                long l => (int)l,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                _ => 0,
            };
        }

        return 0;
    }

    public static async Task RetryOrDeadLetterAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        string queueName,
        int retryLimit,
        ILogger logger,
        CancellationToken ct)
    {
        var attempt = ReadRetryCount(ea.BasicProperties);

        if (attempt >= retryLimit)
        {
            Telemetry.NotificationDeadLetters.Add(1);
            logger.LogError(
                "Transient failure survived {Attempts} redeliveries; dead-lettering message {MessageId}.",
                attempt, ea.BasicProperties.MessageId);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        Telemetry.NotificationRedeliveries.Add(1);
        logger.LogWarning(
            "Transient failure handling message {MessageId}; redelivery {Attempt}/{Limit}.",
            ea.BasicProperties.MessageId, attempt + 1, retryLimit);

        // Preserve every incoming header (traceparent included) and overwrite the counter.
        var headers = ea.BasicProperties.Headers is { } incoming
            ? new Dictionary<string, object?>(incoming)
            : new Dictionary<string, object?>();
        headers[RetryCountHeader] = attempt + 1;

        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = ea.BasicProperties.MessageId,
            CorrelationId = ea.BasicProperties.CorrelationId,
            ContentType = ea.BasicProperties.ContentType,
            Headers = headers,
        };

        try
        {
            // Default exchange routes directly to the queue by name. Publish before ack:
            // if the publish fails, the original stays unacked and broker redelivery
            // takes over — the message is never lost between the two operations.
            await channel.BasicPublishAsync(
                exchange: "",
                routingKey: queueName,
                mandatory: true,
                basicProperties: properties,
                body: ea.Body,
                cancellationToken: ct);

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Failed to republish message {MessageId} for retry; leaving it unacked.",
                ea.BasicProperties.MessageId);
        }
    }
}
