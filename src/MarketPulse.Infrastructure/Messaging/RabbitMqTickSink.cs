using System.Text.Json;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Publishes ticks to the prices exchange, fire and forget. No publisher confirms and no
/// outbox: a tick is superseded a second later, so durability here would buy nothing and
/// cost latency on the path that parallels the SignalR broadcast.
///
/// <see cref="SendAsync"/> never waits on connection establishment. <c>TickBroadcaster</c>
/// awaits every sink in sequence for a given tick, so a call that blocked here would also
/// stall the SignalR sink behind it for as long as the broker stayed unreachable —
/// <c>RabbitMqConnection</c> retries a dead broker with unbounded exponential backoff, which
/// is right for a background reconnect and wrong for anything sitting inline on that reader.
/// If no open channel exists yet, the tick is simply dropped (ticks are lossy by design — the
/// next one is a second away) and, if nothing is already trying, a single background attempt
/// to (re)establish a channel is kicked off. Nobody waits for that attempt; it self-heals the
/// sink for the ticks that follow.
/// </summary>
public sealed class RabbitMqTickSink(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqTickSink> logger) : ITickSink, IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _connectGate = new();

    private IChannel? _channel;
    private Task? _connectTask;
    private volatile bool _disposed;

    public Task SendAsync(PriceTick tick, CancellationToken ct)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        var channel = _channel;

        if (channel is { IsOpen: true })
        {
            return PublishAsync(channel, tick, ct);
        }

        // No channel yet — first tick ever, or the broker dropped a previous connection.
        // Make sure exactly one background attempt is working on getting one back, and
        // return immediately either way. This tick is not published.
        TriggerConnect();
        return Task.CompletedTask;
    }

    private async Task PublishAsync(IChannel channel, PriceTick tick, CancellationToken ct)
    {
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

    private void TriggerConnect()
    {
        if (_disposed)
        {
            return;
        }

        lock (_connectGate)
        {
            if (_disposed || _connectTask is { IsCompleted: false })
            {
                // Either shutting down, or another tick already kicked off an attempt that
                // hasn't finished yet — one attempt in flight at a time, and this call does
                // not wait for it.
                return;
            }

            _connectTask = ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            var channel = await connection.CreateChannelAsync(
                publisherConfirms: false, _shutdownCts.Token);
            await RabbitMqTopology.DeclareAsync(channel, _options, _shutdownCts.Token);

            if (_disposed)
            {
                // Lost the race with DisposeAsync: nothing will ever read this channel
                // through _channel again, so it must be closed here rather than left open.
                await channel.DisposeAsync();
                return;
            }

            _channel = channel;
        }
        catch (OperationCanceledException)
        {
            // Host shutdown interrupted the attempt. Not logged as a failure — this is the
            // expected way a stuck attempt ends, not the broker being unwell.
        }
        catch (Exception ex)
        {
            // Deliberately swallowed rather than rethrown: nobody awaits _connectTask, so an
            // unhandled exception here would otherwise become an unobserved task exception.
            // The sink is not left wedged — the next tick that finds no open channel calls
            // TriggerConnect again and starts a fresh attempt.
            logger.LogWarning(
                ex, "RabbitMqTickSink failed to establish a channel; will retry on the next tick.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_connectGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Interrupt any in-flight connection attempt. ConnectAsync catches the resulting
        // OperationCanceledException itself, so this neither surfaces as an unobserved task
        // exception nor requires DisposeAsync to wait for that task to unwind.
        await _shutdownCts.CancelAsync();

        var channel = Interlocked.Exchange(ref _channel, null);

        if (channel is not null)
        {
            await channel.DisposeAsync();
        }

        _shutdownCts.Dispose();
    }
}
