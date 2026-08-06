using System.Text;
using MarketPulse.Application.Telemetry;
using MarketPulse.Infrastructure.Messaging;
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
///
/// <para>The no-loss claim below depends on the caller's channel having publisher
/// confirmations enabled (<see cref="RabbitMqConsumerService.RequiresPublisherConfirms"/>).
/// Without confirms, <c>BasicPublishAsync</c> returns once the bytes are written to the
/// socket, not once the broker has accepted them, and <c>mandatory: true</c> only reports an
/// unroutable message asynchronously via an event nothing here subscribes to — so an awaited
/// publish that "succeeds" would not actually mean the copy landed, and acking the original
/// right after would lose the message this whole mechanism exists to keep. With confirms on,
/// a publish that the broker did not accept — including as unroutable, since confirms and
/// <c>mandatory</c> compose — surfaces as an exception here, which the catch below correctly
/// treats as "leave the original unacked."</para>
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
                // An unparseable byte[] (or any other shape) falls back to 0 rather than
                // throwing. That is safe, not just permissive: the very next republish
                // overwrites this header with a plain int (see below), so an alien value
                // costs at most one extra retry round before the int counter takes over for
                // good — it does not reopen the unbounded loop this type exists to close.
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
            // if the publish fails, the original stays unacked and broker redelivery takes
            // over — the message is never lost between the two operations. This is only true
            // because the caller's channel has publisher confirmations on: BasicPublishAsync
            // then does not return until the broker has confirmed the message (or thrown a
            // PublishException/PublishReturnException for a nack or an unroutable mandatory
            // publish), so a completed await here is a genuine "the copy landed" guarantee,
            // not just "the bytes left the socket."
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
