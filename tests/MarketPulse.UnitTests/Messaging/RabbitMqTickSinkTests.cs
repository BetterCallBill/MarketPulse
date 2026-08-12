using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

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

    /// <summary>
    /// A hand-rolled counting spy for the one capability <c>RabbitMqTickSink</c> calls on
    /// <c>RabbitMqConnection</c>. <c>RabbitMqConnection</c> is sealed and has no interface
    /// (deliberately — see its own docs), so it cannot be substituted directly; the sink's
    /// internal test-only constructor takes this capability as a plain delegate instead,
    /// which a hand-rolled spy can count without any mocking framework. The returned task
    /// never completes on its own — the test controls exactly when (if ever) an "attempt"
    /// finishes by completing <see cref="Attempts"/> itself.
    /// </summary>
    private sealed class CountingChannelFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource<IChannel> Attempts { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Attempts.Task;
        }
    }

    [Fact]
    public async Task Five_ticks_arriving_while_one_connection_attempt_is_in_flight_trigger_exactly_one_attempt()
    {
        // This is the actual single-flight guarantee: TriggerConnect's
        // "lock (_connectGate) { if (_connectTask is { IsCompleted: false }) return; ... }"
        // guard must stop every tick after the first from starting a competing attempt.
        // Asserting only that SendAsync returns quickly (as the prior version of this test
        // did) does not exercise that guard at all — SendAsync never awaits TriggerConnect's
        // result on any path, so it would return just as fast with the guard deleted.
        var factory = new CountingChannelFactory();

        await using var sink = new RabbitMqTickSink(
            factory.CreateChannelAsync,
            Options.Create(UnreachableBroker),
            NullLogger<RabbitMqTickSink>.Instance);

        var tick = new PriceTick("IVV", 50m, DateTimeOffset.UnixEpoch);

        // factory.Attempts never completes during this loop, so every one of these five
        // ticks arrives while the first (and, if the guard works, only) attempt is still
        // running.
        for (var i = 0; i < 5; i++)
        {
            await sink.SendAsync(tick, CancellationToken.None);
        }

        Assert.Equal(1, factory.Calls);

        // Let the in-flight attempt unwind cleanly rather than leaving it stuck for the
        // rest of the test run.
        factory.Attempts.TrySetException(new InvalidOperationException("test teardown"));
    }

    [Fact]
    public async Task Reconnecting_disposes_the_closed_channel_it_replaces()
    {
        // Same defect as the connection leak fixed in 2cf3e3b, one level down: the field was
        // overwritten rather than swapped, so a sink that reconnected after every broker
        // outage accumulated one unreleased channel per outage.
        var stale = Substitute.For<IChannel>();
        stale.IsOpen.Returns(false);

        var replacement = Substitute.For<IChannel>();
        replacement.IsOpen.Returns(true);

        var handed = new List<IChannel>();

        await using var sink = new RabbitMqTickSink(
            (_, _) =>
            {
                var channel = handed.Count == 0 ? stale : replacement;
                handed.Add(channel);
                return Task.FromResult(channel);
            },
            Options.Create(UnreachableBroker),
            NullLogger<RabbitMqTickSink>.Instance);

        var tick = new PriceTick("IVV", 50m, DateTimeOffset.UnixEpoch);

        // First tick: no channel at all, so the background attempt establishes `stale`.
        await sink.SendAsync(tick, CancellationToken.None);
        await WaitUntilAsync(() => handed.Count == 1, "the first channel");

        // Second tick: `stale` reports itself closed, so a replacement is established — the
        // moment the old one has to be released.
        await sink.SendAsync(tick, CancellationToken.None);
        await WaitUntilAsync(() => handed.Count == 2, "the replacement channel");

        await WaitUntilAsync(
            () => stale.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IChannel.DisposeAsync)),
            "the stale channel to be disposed");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Timed out waiting for {what}.");
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
