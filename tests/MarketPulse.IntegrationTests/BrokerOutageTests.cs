using System.Text;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// The claim slice 4a is built on — "alerts survive a broker outage" — has two halves. The
/// outbox covers the publishing half and the rest of this suite exercises it. This class
/// covers the other half, which had no coverage at all: that the processes reading the queues
/// are still reading them once the broker comes back.
///
/// <para>Both tests run against the shared broker through a <see cref="TcpProxy"/> that the
/// test cuts and reconnects, so the outage is real from the client's point of view without
/// any other test class losing its queues.</para>
/// </summary>
[Collection(nameof(MessagingCollection))]
public class BrokerOutageTests(RabbitMqFixture rabbit)
{
    private RabbitMqOptions ThroughProxy(TcpProxy proxy)
    {
        var uri = new Uri(rabbit.ConnectionString);

        return new RabbitMqOptions
        {
            HostName = "127.0.0.1",
            Port = proxy.Port,
            UserName = uri.UserInfo.Split(':')[0],
            Password = uri.UserInfo.Split(':')[1],

            // Keeps the consumer's resubscribe backoff brisk. The production cap of 30s is
            // right for a real outage and would make this test wait out a doubling sequence
            // it is not trying to assert anything about.
            MaxConnectionRetryDelay = TimeSpan.FromSeconds(2)
        };
    }

    private TcpProxy NewProxy()
    {
        var uri = new Uri(rabbit.ConnectionString);
        return new TcpProxy(uri.Host, uri.Port);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition, string what, int seconds = 30)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Timed out after {seconds}s waiting for {what}.");
    }

    [Fact]
    public async Task A_connection_that_is_only_recovering_is_handed_back_rather_than_disposed()
    {
        // The bug this pins down: AutorecoveringConnection.IsOpen delegates to the inner
        // connection, so it reads false for the entire recovery window. Treating that as
        // "this connection is finished" disposed a connection that was about to come back —
        // and every channel and consumer riding on it — while the caller then blocked in an
        // unbounded reconnect loop against a broker that was still down.
        await using var proxy = NewProxy();
        var options = Options.Create(ThroughProxy(proxy));

        await using var connection = new RabbitMqConnection(
            options, NullLogger<RabbitMqConnection>.Instance);

        var original = await connection.GetAsync(CancellationToken.None);
        Assert.True(original.IsOpen);

        proxy.Sever();
        await WaitUntilAsync(() => !original.IsOpen, "the connection to notice the outage");

        // Two assertions in one await. Same instance: the recovering connection was not
        // thrown away. Returned at all within five seconds: the caller was not parked in
        // OpenWithBackoffAsync waiting for a broker that is still unreachable.
        var duringOutage = await connection
            .GetAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(original, duringOutage);

        proxy.Restore();

        // And because it was left alone, the client's own recovery puts it back — the whole
        // reason not to dispose it.
        await WaitUntilAsync(
            () => original.IsOpen, "the client's automatic recovery to complete", 60);

        await using var channel = await connection.CreateChannelAsync(
            publisherConfirms: false, CancellationToken.None);

        Assert.True(channel.IsOpen);
    }

    private sealed class RecordingConsumer(
        RabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        string queue) : RabbitMqConsumerService(
            connection, options, NullLogger<RecordingConsumer>.Instance)
    {
        private readonly List<string> _received = [];

        public bool Received(string body)
        {
            lock (_received)
            {
                return _received.Contains(body);
            }
        }

        protected override string QueueName => queue;

        protected override async Task HandleAsync(
            IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
        {
            lock (_received)
            {
                _received.Add(Encoding.UTF8.GetString(delivery.Body.Span));
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
    }

    [Fact]
    public async Task A_consumer_resumes_delivering_after_the_broker_connection_is_severed()
    {
        // The headline. Before the supervision loop, a consumer subscribed once and parked on
        // Task.Delay(Timeout.Infinite) forever: the channel died with the connection, nothing
        // resubscribed, and the process kept running, kept logging nothing, and kept
        // reporting healthy while messages piled up durably in a queue nobody was reading.
        await using var proxy = NewProxy();
        var options = ThroughProxy(proxy);

        // A queue of this test's own, so severing a link and asserting on deliveries cannot
        // interact with the queues the rest of MessagingCollection shares.
        var queue = $"outage.test.{Guid.NewGuid():N}";
        var routingKey = $"outage.test.{Guid.NewGuid():N}";

        await using (var admin = await rabbit.ConnectAsync())
        await using (var channel = await admin.CreateChannelAsync())
        {
            await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);

            await channel.QueueDeclareAsync(
                queue, durable: true, exclusive: false, autoDelete: false, arguments: null);

            await channel.QueueBindAsync(queue, options.AlertsExchange, routingKey);
        }

        // Published straight to the broker, not through the proxy — the publisher is not what
        // this test is about, and a message sent mid-outage has to reach the queue so that
        // the consumer has something to find when it comes back.
        async Task PublishAsync(string body)
        {
            await using var producer = await rabbit.ConnectAsync();
            await using var channel = await producer.CreateChannelAsync();

            await channel.BasicPublishAsync(
                exchange: options.AlertsExchange,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: new BasicProperties { Persistent = true },
                body: Encoding.UTF8.GetBytes(body),
                cancellationToken: CancellationToken.None);
        }

        await using var connection = new RabbitMqConnection(
            Options.Create(options), NullLogger<RabbitMqConnection>.Instance);

        var consumer = new RecordingConsumer(connection, Options.Create(options), queue);
        await consumer.StartAsync(CancellationToken.None);

        try
        {
            await PublishAsync("before");
            await WaitUntilAsync(() => consumer.Received("before"), "the first delivery");

            proxy.Sever();

            // Published while the consumer cannot possibly see it. The queue is durable, so
            // the broker holds it until somebody subscribes again.
            await PublishAsync("during");

            proxy.Restore();

            await WaitUntilAsync(
                () => consumer.Received("during"),
                "the message published during the outage to be delivered after it", 60);
        }
        finally
        {
            var stop = consumer.StopAsync(CancellationToken.None);

            // Shutting down promptly is part of the contract: the loop spends its life
            // awaiting a channel shutdown that may never come, and that await has to observe
            // the stopping token or every deploy waits out the host's stop timeout.
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10))));

            consumer.Dispose();
        }
    }
}
