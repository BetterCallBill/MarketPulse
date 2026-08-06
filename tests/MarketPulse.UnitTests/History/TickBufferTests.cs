using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.History;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketPulse.UnitTests.History;

public class TickBufferTests
{
    private static TickBuffer CreateBuffer(int capacity) =>
        new(Options.Create(new HistoryOptions { BufferCapacity = capacity }),
            NullLogger<TickBuffer>.Instance);

    private static PriceTick Tick(decimal price) =>
        new("IVV", price, new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero).AddSeconds((double)price));

    [Fact]
    public async Task Sink_enqueues_without_blocking()
    {
        var buffer = CreateBuffer(capacity: 100);
        var sink = new PersistingTickSink(buffer);

        await sink.SendAsync(Tick(1m), CancellationToken.None);

        Assert.True(buffer.Reader.TryRead(out var tick));
        Assert.Equal(1m, tick.Price);
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_tick()
    {
        var buffer = CreateBuffer(capacity: 100); // Channel enforces a real minimum of 1; we fill past it
        var sink = new PersistingTickSink(buffer);

        for (var i = 0; i < 103; i++)
        {
            await sink.SendAsync(Tick(i), CancellationToken.None);
        }

        Assert.True(buffer.Reader.TryRead(out var oldestSurvivor));
        Assert.Equal(3m, oldestSurvivor.Price); // 0, 1, 2 were dropped
    }
}
