using MarketPulse.Api.RealTime;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketPulse.UnitTests.RealTime;

public class TickBroadcasterTests
{
    private sealed class RecordingSink : ITickSink
    {
        public List<PriceTick> Received { get; } = [];

        public Task SendAsync(PriceTick tick, CancellationToken ct)
        {
            Received.Add(tick);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ITickSink
    {
        public int Calls { get; private set; }

        public Task SendAsync(PriceTick tick, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("broker is down");
        }
    }

    private static PriceTick Tick(string ticker) =>
        new(ticker, 50m, DateTimeOffset.UnixEpoch);

    private static async Task RunAsync(
        PriceTickChannel channel, IEnumerable<ITickSink> sinks, Func<bool> done)
    {
        var broadcaster = new TickBroadcaster(
            channel, sinks, NullLogger<TickBroadcaster>.Instance);

        await broadcaster.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await broadcaster.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Every_sink_receives_every_tick()
    {
        // The bug this test exists to prevent: a Channel<T> DISTRIBUTES items among its
        // readers, it does not broadcast. Two hosted services each reading the channel
        // would split the ticks between them and the dashboard would silently tick at half
        // speed. One reader, many sinks.
        var channel = new PriceTickChannel();
        var first = new RecordingSink();
        var second = new RecordingSink();

        await channel.Writer.WriteAsync(Tick("IVV"));
        await channel.Writer.WriteAsync(Tick("NDQ"));

        await RunAsync(channel, [first, second],
            () => first.Received.Count == 2 && second.Received.Count == 2);

        Assert.Equal(["IVV", "NDQ"], first.Received.Select(t => t.Ticker));
        Assert.Equal(["IVV", "NDQ"], second.Received.Select(t => t.Ticker));
    }

    [Fact]
    public async Task A_failing_sink_does_not_stop_the_others_or_the_reader()
    {
        // The broker being unwell must not degrade the dashboard.
        var channel = new PriceTickChannel();
        var broken = new ThrowingSink();
        var healthy = new RecordingSink();

        await channel.Writer.WriteAsync(Tick("IVV"));
        await channel.Writer.WriteAsync(Tick("NDQ"));

        await RunAsync(channel, [broken, healthy], () => healthy.Received.Count == 2);

        Assert.Equal(2, healthy.Received.Count);
        Assert.Equal(2, broken.Calls);
    }
}
