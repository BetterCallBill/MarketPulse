using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// The supervision loop every RabbitMQ consumer in the system runs on: subscribe, park until
/// the channel dies, throw the channel away, subscribe again.
///
/// <para>Written because the alternative — subscribe once and park on
/// <c>Task.Delay(Timeout.Infinite)</c> forever — makes a consumer's liveness entirely
/// dependent on somebody else's connection object surviving. One consumer whose channel is
/// gone is not a loud failure: nothing throws, nothing logs, <c>/health</c> stays green, and
/// alerts simply pile up durably in a queue nobody is reading. A loop that owns its own
/// subscription cannot fail that way, and it also covers the failures automatic recovery
/// never claimed to: a channel-level error that closes only the channel, and a connection the
/// client has genuinely given up on.</para>
///
/// <para>Disposing the old channel before resubscribing is load-bearing rather than tidy.
/// The client records every channel it opens and re-runs its consumers on recovery;
/// <c>AutorecoveringChannel.AutomaticallyRecoverAsync</c> declines to recover a disposed one
/// (and the connection then drops it from its recorded set), so disposing first is what stops
/// this loop and topology recovery from both resubscribing and double-delivering every
/// message.</para>
///
/// <para><see cref="ExecuteAsync"/> is sealed and catches everything. An exception escaping it
/// would meet .NET's default <c>BackgroundServiceExceptionBehavior.StopHost</c> and take the
/// whole host down over a broker that is merely restarting — which the design spec's
/// "the broker being down must not take the API down with it" rules out.</para>
/// </summary>
public abstract class RabbitMqConsumerService : BackgroundService
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);

    private readonly Func<bool, CancellationToken, Task<IChannel>> _createChannel;
    private readonly ILogger _logger;
    private readonly TimeSpan _firstRetryDelay;

    protected RabbitMqConsumerService(
        RabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger logger)
        : this(connection.CreateChannelAsync, options, logger, FirstRetryDelay)
    {
    }

    /// <summary>
    /// Test-only seam, the same one <see cref="RabbitMqTickSink"/> uses and for the same
    /// reason: <see cref="RabbitMqConnection"/> is sealed with no interface, so a test cannot
    /// substitute it, but it can substitute the single capability this loop calls on it. The
    /// retry delay is parameterised alongside it because a loop that waits a second between
    /// attempts is right for a broker restart and far too slow for a test that wants to watch
    /// two of them; the doubling and the
    /// <see cref="RabbitMqOptions.MaxConnectionRetryDelay"/> cap stay the production ones.
    /// </summary>
    internal RabbitMqConsumerService(
        Func<bool, CancellationToken, Task<IChannel>> createChannel,
        IOptions<RabbitMqOptions> options,
        ILogger logger,
        TimeSpan firstRetryDelay)
    {
        _createChannel = createChannel;
        _logger = logger;
        _firstRetryDelay = firstRetryDelay;
        Options = options.Value;
    }

    protected RabbitMqOptions Options { get; }

    /// <summary>The queue this consumer subscribes to.</summary>
    protected abstract string QueueName { get; }

    /// <summary>
    /// Handles one delivery. Implementations own their own acking and their own failure
    /// classification — this base class deliberately does not ack for them, because "when is
    /// this message safely handled?" is the one question only the consumer can answer.
    /// </summary>
    protected abstract Task HandleAsync(
        IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService runs ExecuteAsync inline until its first await, so without this a
        // broker that is slow to answer would delay host startup.
        await Task.Yield();

        var retryDelay = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            IChannel? channel = null;

            try
            {
                if (retryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(retryDelay, stoppingToken);
                }

                channel = await _createChannel(false, stoppingToken);

                await RabbitMqTopology.DeclareAsync(channel, Options, stoppingToken);

                // Without a prefetch limit the broker pushes the whole queue at us and the
                // TTL stops protecting anything — the messages would already be in our process.
                await channel.BasicQosAsync(
                    0, Options.PrefetchCount, global: false, stoppingToken);

                // Subscribed before BasicConsumeAsync so a channel that dies during the
                // subscribe still trips this rather than leaving the loop parked forever.
                var shutdown = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                channel.ChannelShutdownAsync += (_, _) =>
                {
                    shutdown.TrySetResult();
                    return Task.CompletedTask;
                };

                var subscribed = channel;
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) =>
                    HandleAsync(subscribed, delivery, stoppingToken);

                await channel.BasicConsumeAsync(
                    QueueName, autoAck: false, consumer, stoppingToken);

                // A subscription that got this far resets the backoff, so an outage that
                // lasted an hour does not leave the next one starting at the cap. The
                // floor below (after the shutdown await) is what stops this reset from
                // enabling a hot loop when channels die immediately after subscribing.
                retryDelay = TimeSpan.Zero;

                _logger.LogInformation(
                    "{Consumer} listening on {Queue}.", GetType().Name, QueueName);

                await shutdown.Task.WaitAsync(stoppingToken);

                // A floor of one delay, not another zero: a channel that dies
                // immediately after every successful subscribe must not turn this loop
                // hot. One first-retry delay is invisible during a real outage and
                // removes the spin class entirely.
                retryDelay = _firstRetryDelay;

                _logger.LogWarning(
                    "{Consumer}'s channel on {Queue} shut down; resubscribing.",
                    GetType().Name, QueueName);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The host is stopping. Not a failure, and the loop condition ends it.
            }
            catch (Exception ex)
            {
                retryDelay = NextDelay(retryDelay);

                _logger.LogWarning(
                    ex, "{Consumer} could not subscribe to {Queue}; retrying in {Delay}.",
                    GetType().Name, QueueName, retryDelay);
            }
            finally
            {
                if (channel is not null)
                {
                    try
                    {
                        await channel.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        // A channel whose connection died can throw on the way out. That must
                        // never be the reason the next subscription is not attempted.
                        _logger.LogDebug(
                            ex, "{Consumer} failed to dispose a spent channel.", GetType().Name);
                    }
                }
            }
        }
    }

    private TimeSpan NextDelay(TimeSpan current) =>
        current <= TimeSpan.Zero
            ? _firstRetryDelay
            : TimeSpan.FromMilliseconds(Math.Min(
                current.TotalMilliseconds * 2,
                Options.MaxConnectionRetryDelay.TotalMilliseconds));
}
