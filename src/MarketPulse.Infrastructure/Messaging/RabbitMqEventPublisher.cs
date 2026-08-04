using System.Text;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Publishes to the alerts exchange on a confirming channel. With publisher confirmation
/// tracking enabled, BasicPublishAsync does not complete until the broker acks — so an
/// awaited call that returns without throwing is a genuine durability guarantee, and a
/// throw is the dispatcher's signal to leave the row pending.
/// </summary>
public sealed class RabbitMqEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly Func<bool, CancellationToken, Task<IChannel>> _createChannel;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private volatile bool _disposed;

    public RabbitMqEventPublisher(
        RabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqEventPublisher> logger)
        : this(connection.CreateChannelAsync, options, logger)
    {
    }

    /// <summary>
    /// Test-only seam, the same one <see cref="RabbitMqTickSink"/> uses.
    /// <see cref="RabbitMqConnection"/> is sealed with no interface, so a test that wants to
    /// watch what this publisher does with the channel it is handed substitutes the one
    /// capability it asks for instead.
    /// </summary>
    internal RabbitMqEventPublisher(
        Func<bool, CancellationToken, Task<IChannel>> createChannel,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _createChannel = createChannel;
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync(
        string routingKey,
        Guid messageId,
        string payload,
        string? correlationId,
        CancellationToken ct)
    {
        // Checked here so a publish that races shutdown fails as this object being gone,
        // rather than as an ObjectDisposedException from a semaphore the caller has never
        // heard of. RabbitMqTickSink already guards itself this way.
        ObjectDisposedException.ThrowIf(_disposed, this);

        var channel = await ChannelAsync(ct);

        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = messageId.ToString(),
            CorrelationId = correlationId,
            ContentType = "application/json"
        };

        await channel.BasicPublishAsync(
            exchange: _options.AlertsExchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payload),
            cancellationToken: ct);
    }

    private async Task<IChannel> ChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _gate.WaitAsync(ct);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            // The closed channel is still an open AMQP object graph until something releases
            // it. Overwriting the field without disposing leaks one channel per broker
            // outage, which is the same defect already fixed for connections. A channel whose
            // connection died can throw on the way out; that must not stop the replacement
            // being established, which is the whole reason this is not just `await Dispose`.
            if (_channel is { } stale)
            {
                _channel = null;

                try
                {
                    await stale.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose the stale publisher channel.");
                }
            }

            var channel = await _createChannel(true, ct);
            await RabbitMqTopology.DeclareAsync(channel, _options, ct);

            _channel = channel;
            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Set before the gate is disposed so a concurrent PublishAsync sees "this publisher
        // is gone" instead of tripping over a disposed semaphore.
        _disposed = true;

        if (_channel is not null)
        {
            try
            {
                await _channel.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Failed to dispose the publisher channel during shutdown.");
            }

            _channel = null;
        }

        _gate.Dispose();
    }
}
