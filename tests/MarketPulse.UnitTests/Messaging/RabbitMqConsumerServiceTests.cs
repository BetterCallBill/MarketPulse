using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.UnitTests.Messaging;

/// <summary>
/// The consumers' liveness under a broker that comes and goes. Before this base class existed,
/// both consumers subscribed once and then parked on <c>Task.Delay(Timeout.Infinite)</c>
/// forever, which made them depend entirely on a shared connection object surviving every
/// outage — and gave them no way at all to notice a channel that had shut down underneath
/// them. These are the three properties that dependency used to be missing.
/// </summary>
public class RabbitMqConsumerServiceTests
{
    private static IOptions<RabbitMqOptions> TestOptions() =>
        Options.Create(new RabbitMqOptions
        {
            // The cap on the retry backoff. Small so a test that wants to watch two attempts
            // does not spend a second waiting between them.
            MaxConnectionRetryDelay = TimeSpan.FromMilliseconds(20)
        });

    private const string Queue = "test.queue";

    private sealed class TestConsumer(Func<bool, CancellationToken, Task<IChannel>> createChannel)
        : RabbitMqConsumerService(
            createChannel, TestOptions(), NullLogger.Instance, TimeSpan.FromMilliseconds(5))
    {
        protected override string QueueName => Queue;

        protected override Task HandleAsync(
            IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// A substitute channel that is open and reports its subscribed consumer, so a test can
    /// tell "resubscribed" from "still holding the dead one".
    /// </summary>
    private static IChannel OpenChannel()
    {
        var channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        return channel;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>
    /// Every argument is a matcher, including the two the assertion actually cares about:
    /// NSubstitute refuses to mix literals and matchers across arguments of the same type.
    /// </summary>
    private static Task<string> SubscribedTo(IChannel channel) =>
        channel.Received(1).BasicConsumeAsync(
            Arg.Is(Queue), Arg.Is(false), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<IDictionary<string, object?>>(), Arg.Any<IAsyncBasicConsumer>(),
            Arg.Any<CancellationToken>());

    [Fact]
    public async Task A_channel_that_shuts_down_is_disposed_and_replaced_by_a_fresh_subscription()
    {
        // The failure this prevents: RabbitMQ closes every channel on a connection when the
        // connection drops, and nothing in the old code noticed. The consumer stayed alive,
        // logged nothing, and never received another message.
        var first = OpenChannel();
        var second = OpenChannel();

        var handed = new List<IChannel>();

        Task<IChannel> Create(bool _, CancellationToken __)
        {
            var channel = handed.Count == 0 ? first : second;
            handed.Add(channel);
            return Task.FromResult(channel);
        }

        var consumer = new TestConsumer(Create);
        await consumer.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(() => handed.Count == 1, "the first subscription");
            await SubscribedTo(first);

            first.ChannelShutdownAsync += Raise.Event<AsyncEventHandler<ShutdownEventArgs>>(
                first,
                new ShutdownEventArgs(ShutdownInitiator.Library, 541, "connection lost"));

            await WaitUntilAsync(() => handed.Count == 2, "the replacement subscription");

            // Subscribed again, on a channel that is actually alive.
            await SubscribedTo(second);

            // And the dead one released. Disposing it is not tidiness: the client records
            // every channel it opens and re-runs its consumers on recovery, so a channel left
            // undisposed would come back with a second copy of this consumer on it.
            await first.Received(1).DisposeAsync();
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }
    }

    [Fact]
    public async Task A_failure_to_subscribe_neither_escapes_the_service_nor_stops_it_retrying()
    {
        // An exception out of ExecuteAsync meets BackgroundServiceExceptionBehavior.StopHost
        // and takes the whole API down over a broker that is merely restarting — which the
        // spec's "the broker being down must not take the API down with it" rules out.
        var attempts = 0;
        var channel = OpenChannel();

        Task<IChannel> Create(bool _, CancellationToken __)
        {
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                throw new InvalidOperationException("broker is down");
            }

            return Task.FromResult(channel);
        }

        var consumer = new TestConsumer(Create);
        await consumer.StartAsync(CancellationToken.None);

        try
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref attempts) >= 3, "the loop to retry past two failures");

            await SubscribedTo(channel);

            // The service is still running: nothing propagated out of ExecuteAsync, so
            // nothing would have stopped the host.
            Assert.NotNull(consumer.ExecuteTask);
            Assert.False(consumer.ExecuteTask!.IsFaulted);
            Assert.Null(consumer.ExecuteTask.Exception);
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
        }
    }

    [Fact]
    public async Task Stopping_the_service_while_it_is_parked_on_a_live_subscription_returns_promptly()
    {
        // The loop spends almost all of its life awaiting a channel shutdown that may never
        // come. If that await did not observe the stopping token, every shutdown would sit
        // out the host's stop timeout instead.
        var channel = OpenChannel();
        var handed = 0;

        var consumer = new TestConsumer((_, _) =>
        {
            Interlocked.Increment(ref handed);
            return Task.FromResult(channel);
        });

        await consumer.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref handed) == 1, "the subscription");

        var stop = consumer.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(stop, completed);
        consumer.Dispose();
    }
}
