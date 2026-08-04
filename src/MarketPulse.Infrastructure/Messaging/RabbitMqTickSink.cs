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
public sealed class RabbitMqTickSink : ITickSink, IAsyncDisposable
{
    private readonly Func<bool, CancellationToken, Task<IChannel>> _createChannel;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqTickSink> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly object _connectGate = new();

    private IChannel? _channel;
    private Task? _connectTask;
    private volatile bool _disposed;

    public RabbitMqTickSink(
        RabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTickSink> logger)
        : this(connection.CreateChannelAsync, options, logger)
    {
    }

    /// <summary>
    /// Test-only seam. <see cref="RabbitMqConnection"/> is sealed with no interface (by
    /// design — see its own docs), so a counting or failing spy cannot substitute for it
    /// directly. This overload lets a test substitute just the one capability this sink
    /// actually calls — <c>CreateChannelAsync</c> — to assert single-flight behaviour
    /// (exactly one attempt per outage, however many ticks arrive while it is in flight)
    /// without changing <see cref="RabbitMqConnection"/> or the production constructor
    /// above, which callers through DI still use unchanged.
    /// </summary>
    internal RabbitMqTickSink(
        Func<bool, CancellationToken, Task<IChannel>> createChannel,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqTickSink> logger)
    {
        _createChannel = createChannel;
        _options = options.Value;
        _logger = logger;
    }

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
            var channel = await _createChannel(false, _shutdownCts.Token);
            await RabbitMqTopology.DeclareAsync(channel, _options, _shutdownCts.Token);

            if (_disposed)
            {
                // Lost the race with DisposeAsync: nothing will ever read this channel
                // through _channel again, so it must be closed here rather than left open.
                await channel.DisposeAsync();
                return;
            }

            // Whatever was here was closed — that is the only reason this method ran — but a
            // closed channel is still an unreleased AMQP object. Overwriting the field
            // without disposing leaks one per broker outage, the same defect already fixed
            // for connections. Swapped first so the sink starts publishing on the new
            // channel immediately rather than waiting on the old one's teardown, and the
            // teardown itself is caught: a channel whose connection died can throw on the way
            // out, and that must not cost the sink the channel it just established.
            var stale = Interlocked.Exchange(ref _channel, channel);

            if (stale is not null)
            {
                try
                {
                    await stale.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose the stale tick-sink channel.");
                }
            }
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
            _logger.LogWarning(
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
