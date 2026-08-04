using System.Text;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Publishes to the alerts exchange on a confirming channel. With publisher confirmation
/// tracking enabled, BasicPublishAsync does not complete until the broker acks — so an
/// awaited call that returns without throwing is a genuine durability guarantee, and a
/// throw is the dispatcher's signal to leave the row pending.
/// </summary>
public sealed class RabbitMqEventPublisher(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options) : IEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(
        string routingKey,
        Guid messageId,
        string payload,
        string? correlationId,
        CancellationToken ct)
    {
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
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            _channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
            await RabbitMqTopology.DeclareAsync(_channel, _options, ct);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }
}
