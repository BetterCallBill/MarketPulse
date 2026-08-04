using System.Text.Json;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Publishes ticks to the prices exchange, fire and forget. No publisher confirms and no
/// outbox: a tick is superseded a second later, so durability here would buy nothing and
/// cost latency on the path that parallels the SignalR broadcast.
/// </summary>
public sealed class RabbitMqTickSink(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options) : ITickSink, IAsyncDisposable
{
    // RabbitMqConnection retries a broker outage with unbounded exponential backoff (up to
    // MaxConnectionRetryDelay per attempt, forever) — correct for a background reconnect,
    // wrong for a call sitting inline on TickBroadcaster's single reader thread.
    // TickBroadcaster awaits sinks one at a time, so a ChannelAsync call that never returns
    // would freeze the SignalR sink behind it too, for as long as the broker stayed down —
    // exactly the "sink is skipped" property the design is meant to guarantee, broken.
    // Bounding the connection attempt here turns an unreachable broker into a fixed, small
    // per-tick cost that TickBroadcaster's existing per-sink catch can log and skip.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task SendAsync(PriceTick tick, CancellationToken ct)
    {
        var channel = await ChannelAsync(ct);

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new PriceTickMessage(tick.Ticker, tick.Price, tick.TimestampUtc));

        await channel.BasicPublishAsync(
            exchange: _options.PricesExchange,
            routingKey: RabbitMqTopology.RoutingKeyFor(tick.Ticker),
            mandatory: false,
            basicProperties: new BasicProperties
            {
                Persistent = false,

                // A tick has no originating HTTP request to inherit a correlation id from,
                // so it starts one. That id then rides tick -> worker -> outbox row ->
                // alert event -> API consumer, which is the whole causal chain of one
                // notification in a single searchable value.
                CorrelationId = Guid.NewGuid().ToString()
            },
            body: body,
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

            using var timeoutCts = new CancellationTokenSource(ConnectTimeout);
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                _channel = await connection.CreateChannelAsync(
                    publisherConfirms: false, linked.Token);
                await RabbitMqTopology.DeclareAsync(_channel, _options, linked.Token);
            }
            catch (OperationCanceledException) when (
                timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // A genuine caller cancellation (host shutdown) is rethrown unchanged so
                // TickBroadcaster's outer catch can still tell "stopping" apart from "the
                // broker is unwell" — only this sink's own connect timeout is translated
                // into a plain exception so the per-sink catch logs and skips it.
                throw new TimeoutException(
                    $"Timed out connecting to RabbitMQ within {ConnectTimeout}.");
            }

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
