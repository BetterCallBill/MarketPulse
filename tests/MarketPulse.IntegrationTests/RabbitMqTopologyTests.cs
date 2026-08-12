using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(MessagingCollection))]
public class RabbitMqTopologyTests(RabbitMqFixture rabbit)
{
    private static RabbitMqOptions Options() => new();

    [Fact]
    public async Task Declaring_the_topology_twice_succeeds()
    {
        // Both the API and the worker declare at startup, in whatever order they happen to
        // boot. AMQP declarations are idempotent; this proves ours actually are, which
        // means neither process has to wait for the other.
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        await RabbitMqTopology.DeclareAsync(channel, Options(), CancellationToken.None);
        await RabbitMqTopology.DeclareAsync(channel, Options(), CancellationToken.None);
    }

    [Fact]
    public async Task A_tick_published_to_the_prices_exchange_lands_on_the_alerts_queue()
    {
        var options = Options();

        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        await channel.QueuePurgeAsync(options.PricesQueue);

        await channel.BasicPublishAsync(
            exchange: options.PricesExchange,
            routingKey: RabbitMqTopology.RoutingKeyFor("IVV"),
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = false },
            body: "{}"u8.ToArray(),
            cancellationToken: CancellationToken.None);

        var delivered = await channel.BasicGetAsync(options.PricesQueue, autoAck: true);

        Assert.NotNull(delivered);
    }

    [Fact]
    public async Task A_rejected_notification_message_reaches_the_dead_letter_queue()
    {
        var options = Options();

        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        await channel.QueuePurgeAsync(options.NotificationsQueue);
        await channel.QueuePurgeAsync(options.NotificationsDeadLetterQueue);

        await channel.BasicPublishAsync(
            exchange: options.AlertsExchange,
            routingKey: RabbitMqTopology.AlertTriggeredRoutingKey,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true },
            body: "not json"u8.ToArray(),
            cancellationToken: CancellationToken.None);

        var delivered = await channel.BasicGetAsync(options.NotificationsQueue, autoAck: false);
        Assert.NotNull(delivered);

        // Reject without requeue — the policy Task 9 applies to a poison message. The
        // dead-letter exchange is what must catch it.
        await channel.BasicNackAsync(delivered!.DeliveryTag, multiple: false, requeue: false);

        var deadLettered = await WaitForDeadLetterAsync(channel, options);

        Assert.NotNull(deadLettered);
    }

    private static async Task<BasicGetResult?> WaitForDeadLetterAsync(
        IChannel channel, RabbitMqOptions options)
    {
        // Dead-lettering is asynchronous inside the broker, so poll rather than assume.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await channel.BasicGetAsync(
                options.NotificationsDeadLetterQueue, autoAck: true);

            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100);
        }

        return null;
    }
}
