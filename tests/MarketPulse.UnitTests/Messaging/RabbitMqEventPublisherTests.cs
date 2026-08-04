using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace MarketPulse.UnitTests.Messaging;

public class RabbitMqEventPublisherTests
{
    private static readonly IOptions<RabbitMqOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new RabbitMqOptions());

    private static IChannel Channel(bool open)
    {
        var channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(open);
        return channel;
    }

    private static Task Publish(RabbitMqEventPublisher publisher) =>
        publisher.PublishAsync(
            RabbitMqTopology.AlertTriggeredRoutingKey, Guid.NewGuid(), "{}", "corr",
            CancellationToken.None);

    [Fact]
    public async Task A_closed_channel_is_disposed_when_it_is_replaced()
    {
        // The connection-level version of this leak was already fixed; the channel-level one
        // was not. Every broker outage left one unreleased channel behind, because the field
        // was overwritten rather than swapped.
        var stale = Channel(open: false);
        var replacement = Channel(open: true);

        var handed = new List<IChannel>();

        await using var publisher = new RabbitMqEventPublisher(
            (_, _) =>
            {
                var channel = handed.Count == 0 ? stale : replacement;
                handed.Add(channel);
                return Task.FromResult(channel);
            },
            Options,
            NullLogger<RabbitMqEventPublisher>.Instance);

        await Publish(publisher);

        // The first channel reports itself closed, so the second publish must establish a
        // replacement — which is exactly the moment the old one has to be released.
        await Publish(publisher);

        Assert.Equal(2, handed.Count);
        await stale.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task A_dispose_failure_does_not_stop_the_replacement_channel_being_established()
    {
        var stale = Channel(open: false);
        stale.DisposeAsync().Returns(ValueTask.FromException(new IOException("connection gone")));

        var replacement = Channel(open: true);
        var handed = new List<IChannel>();

        await using var publisher = new RabbitMqEventPublisher(
            (_, _) =>
            {
                var channel = handed.Count == 0 ? stale : replacement;
                handed.Add(channel);
                return Task.FromResult(channel);
            },
            Options,
            NullLogger<RabbitMqEventPublisher>.Instance);

        await Publish(publisher);
        await Publish(publisher);

        Assert.Equal(2, handed.Count);
    }

    [Fact]
    public async Task Publishing_after_disposal_reports_the_publisher_as_disposed_not_a_semaphore()
    {
        // Both versions throw ObjectDisposedException; only one of them names something the
        // caller has heard of. Without the flag the exception comes from the internal
        // SemaphoreSlim, which tells a reader nothing about what actually went wrong.
        var publisher = new RabbitMqEventPublisher(
            (_, _) => Task.FromResult(Channel(open: true)),
            Options,
            NullLogger<RabbitMqEventPublisher>.Instance);

        await publisher.DisposeAsync();

        var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => Publish(publisher));

        Assert.Equal(typeof(RabbitMqEventPublisher).FullName, ex.ObjectName);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        var channel = Channel(open: true);

        var publisher = new RabbitMqEventPublisher(
            (_, _) => Task.FromResult(channel),
            Options,
            NullLogger<RabbitMqEventPublisher>.Instance);

        await Publish(publisher);

        await publisher.DisposeAsync();
        await publisher.DisposeAsync();

        // The second call must not reach the already-disposed semaphore.
        await channel.Received(1).DisposeAsync();
    }
}
