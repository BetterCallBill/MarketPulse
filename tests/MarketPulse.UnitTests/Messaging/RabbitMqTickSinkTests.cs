using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketPulse.UnitTests.Messaging;

public class RabbitMqTickSinkTests
{
    // Nothing listens here: an unused loopback port, so a connection attempt against it
    // fails (or at least never succeeds) rather than racing a real broker that might
    // happen to be running wherever this test executes.
    private static readonly RabbitMqOptions UnreachableBroker = new()
    {
        HostName = "127.0.0.1",
        Port = 57123,
        MaxConnectionRetryDelay = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public async Task A_tick_arriving_with_no_open_channel_returns_promptly_instead_of_waiting_on_connection_establishment()
    {
        // RabbitMqConnection retries a failed connection with exponential backoff forever —
        // it never gives up and never throws just because the broker is unreachable. Before
        // this fix, RabbitMqTickSink awaited that connection attempt inline (bounded only by
        // a 2-second timeout in the prior revision, unbounded in the original brief), so
        // SendAsync would still be running well past the deadline below. The fix means the
        // very first tick — before any channel exists — must return immediately regardless
        // of whether the broker ever answers.
        var options = Options.Create(UnreachableBroker);

        await using var connection = new RabbitMqConnection(
            options, NullLogger<RabbitMqConnection>.Instance);

        await using var sink = new RabbitMqTickSink(
            connection, options, NullLogger<RabbitMqTickSink>.Instance);

        var tick = new PriceTick("IVV", 50m, DateTimeOffset.UnixEpoch);

        var sendTask = sink.SendAsync(tick, CancellationToken.None);
        var completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

        Assert.Same(sendTask, completed);
    }

    [Fact]
    public async Task A_second_tick_while_a_connection_attempt_is_in_flight_also_returns_promptly()
    {
        // Single-flight: the second tick must not start a competing attempt, and must not
        // wait for the one already running — it should return just as fast as the first.
        var options = Options.Create(UnreachableBroker);

        await using var connection = new RabbitMqConnection(
            options, NullLogger<RabbitMqConnection>.Instance);

        await using var sink = new RabbitMqTickSink(
            connection, options, NullLogger<RabbitMqTickSink>.Instance);

        var first = new PriceTick("IVV", 50m, DateTimeOffset.UnixEpoch);
        var second = new PriceTick("NDQ", 60m, DateTimeOffset.UnixEpoch);

        await sink.SendAsync(first, CancellationToken.None);

        var sendTask = sink.SendAsync(second, CancellationToken.None);
        var completed = await Task.WhenAny(sendTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

        Assert.Same(sendTask, completed);
    }

    [Fact]
    public async Task Disposing_the_sink_while_a_connection_attempt_is_in_flight_does_not_hang()
    {
        var options = Options.Create(UnreachableBroker);

        await using var connection = new RabbitMqConnection(
            options, NullLogger<RabbitMqConnection>.Instance);

        var sink = new RabbitMqTickSink(
            connection, options, NullLogger<RabbitMqTickSink>.Instance);

        await sink.SendAsync(
            new PriceTick("IVV", 50m, DateTimeOffset.UnixEpoch), CancellationToken.None);

        var disposeTask = sink.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(disposeTask, completed);
    }
}
