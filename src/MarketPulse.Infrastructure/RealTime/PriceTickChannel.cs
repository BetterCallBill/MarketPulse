using System.Threading.Channels;
using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The seam between tick production and tick delivery. Slice 1 fills it with
/// FakeTickService; Phase 2 replaces only the producer, leaving every consumer intact.
/// </summary>
public sealed class PriceTickChannel
{
    private readonly Channel<PriceTick> _channel =
        Channel.CreateBounded<PriceTick>(new BoundedChannelOptions(capacity: 1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelWriter<PriceTick> Writer => _channel.Writer;
    public ChannelReader<PriceTick> Reader => _channel.Reader;
}
