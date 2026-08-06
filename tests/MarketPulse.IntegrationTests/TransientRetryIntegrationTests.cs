using System.Text;
using MarketPulse.Api.Messaging;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(MessagingCollection))]
public class TransientRetryIntegrationTests(RabbitMqFixture rabbit)
{
    private static RabbitMqOptions Options() => new();

    [Fact]
    public async Task Below_cap_republish_lands_back_on_the_queue_with_incremented_header()
    {
        var options = Options();

        await using var connection = await rabbit.ConnectAsync();
        // Publisher confirms on, matching the real channel TransientRetry runs on
        // (RabbitMqConsumerService.RequiresPublisherConfirms for AlertTriggeredConsumer):
        // without confirmation tracking, RetryOrDeadLetterAsync's awaited republish never
        // exercises the confirm-await path it depends on in production.
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        await channel.QueuePurgeAsync(options.NotificationsQueue);

        // Seed one message with x-retry-count = 1
        var seed = new BasicProperties
        {
            MessageId = "it-1",
            Headers = new Dictionary<string, object?> { [TransientRetry.RetryCountHeader] = 1 },
        };
        await channel.BasicPublishAsync("", options.NotificationsQueue, true, seed,
            Encoding.UTF8.GetBytes("{}"));

        // autoAck: false — TransientRetry does its own ack/nack on this delivery tag below,
        // exactly like the real consumer; an auto-acked tag would already be invalid by then.
        var delivered = await BasicGetWithRetryAsync(channel, options.NotificationsQueue, autoAck: false);
        var ea = new BasicDeliverEventArgs(
            "tag", delivered.DeliveryTag, false, "", options.NotificationsQueue,
            delivered.BasicProperties, delivered.Body);

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, options.NotificationsQueue, retryLimit: 5,
            NullLogger.Instance, CancellationToken.None);

        var redelivered = await BasicGetWithRetryAsync(channel, options.NotificationsQueue, autoAck: true);
        Assert.Equal(2, TransientRetry.ReadRetryCount(redelivered.BasicProperties));
    }

    [Fact]
    public async Task At_cap_message_lands_in_the_dead_letter_queue()
    {
        var options = Options();

        await using var connection = await rabbit.ConnectAsync();
        // Publisher confirms on, matching the real channel TransientRetry runs on
        // (RabbitMqConsumerService.RequiresPublisherConfirms for AlertTriggeredConsumer):
        // without confirmation tracking, RetryOrDeadLetterAsync's awaited republish never
        // exercises the confirm-await path it depends on in production.
        await using var channel = await connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        await channel.QueuePurgeAsync(options.NotificationsQueue);
        await channel.QueuePurgeAsync(options.NotificationsDeadLetterQueue);

        var seed = new BasicProperties
        {
            MessageId = "it-2",
            Headers = new Dictionary<string, object?> { [TransientRetry.RetryCountHeader] = 5 },
        };
        await channel.BasicPublishAsync("", options.NotificationsQueue, true, seed,
            Encoding.UTF8.GetBytes("{}"));

        var delivered = await BasicGetWithRetryAsync(channel, options.NotificationsQueue, autoAck: false);
        var ea = new BasicDeliverEventArgs(
            "tag", delivered.DeliveryTag, false, "", options.NotificationsQueue,
            delivered.BasicProperties, delivered.Body);

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, options.NotificationsQueue, retryLimit: 5,
            NullLogger.Instance, CancellationToken.None);

        var dead = await BasicGetWithRetryAsync(channel, options.NotificationsDeadLetterQueue, autoAck: true);
        Assert.Equal("it-2", dead.BasicProperties.MessageId);
    }

    private static async Task<BasicGetResult> BasicGetWithRetryAsync(
        IChannel channel, string queue, bool autoAck)
    {
        for (var i = 0; i < 50; i++)
        {
            var result = await channel.BasicGetAsync(queue, autoAck);
            if (result is not null) return result;
            await Task.Delay(100);
        }

        throw new TimeoutException($"No message appeared on {queue} within 5s.");
    }
}
