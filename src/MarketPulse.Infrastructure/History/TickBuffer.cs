using System.Threading.Channels;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The write path's own buffer, separate from PriceTickChannel: the broadcast loop must
/// never wait on the database, so the sink enqueues here and TickPersistenceService drains
/// on its own clock. DropOldest under overflow — a sustained database outage costs a hole
/// in history, never a stalled live stream.
/// </summary>
public sealed class TickBuffer
{
    private readonly Channel<PriceTick> _channel;

    public TickBuffer(IOptions<HistoryOptions> options, ILogger<TickBuffer> logger)
    {
        _channel = Channel.CreateBounded<PriceTick>(
            new BoundedChannelOptions(options.Value.BufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            dropped => logger.LogWarning(
                "Tick buffer full; dropped oldest tick {Ticker}@{TimestampUtc:u}.",
                dropped.Ticker, dropped.TimestampUtc));
    }

    public ChannelWriter<PriceTick> Writer => _channel.Writer;
    public ChannelReader<PriceTick> Reader => _channel.Reader;
}
