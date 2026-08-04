using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// One lazily-opened, shared connection per process. A broker that is not up yet must not
/// crash-loop the host: the API's alert CRUD is a database operation and keeps working
/// perfectly well while the broker is missing, and the worker's outbox rows simply
/// accumulate until it returns.
/// </summary>
public sealed class RabbitMqConnection(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;
    private volatile bool _disposed;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { } live && IsStillOurs(live))
        {
            return live;
        }

        // Linked so a shutdown mid-backoff cancels this attempt instead of leaving
        // DisposeAsync waiting on the gate for the remainder of the retry delay.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);

        await _gate.WaitAsync(linked.Token);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is { } stillLive && IsStillOurs(stillLive))
            {
                return stillLive;
            }

            if (_connection is not null)
            {
                // Only reached for a connection the client has given up on (see
                // IsStillOurs). The old object and any channels it still owns must be
                // released before it is replaced, or every outage leaks one connection.
                // A connection that died abnormally may itself throw on dispose; that must
                // not stop us from opening its replacement.
                try
                {
                    await _connection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose the stale RabbitMQ connection.");
                }
                finally
                {
                    _connection = null;
                }
            }

            _connection = await OpenWithBackoffAsync(linked.Token);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether the cached connection is still the one callers should be using — either open,
    /// or closed but being recovered in place by the client.
    ///
    /// <para><c>IsOpen == false</c> is emphatically <em>not</em> "dead". With
    /// <c>AutomaticRecoveryEnabled</c>, <c>CreateConnectionAsync</c> hands back an
    /// <c>AutorecoveringConnection</c> whose <c>IsOpen</c> delegates straight to the inner
    /// connection, so it reads false for the whole recovery window — <c>NetworkRecoveryInterval</c>
    /// (5s by default) per attempt, retried indefinitely. Disposing on that signal destroys a
    /// connection that was about to come back, and takes every channel and consumer on it with
    /// it; the consumers then never resubscribe and the pipeline dies silently. That is exactly
    /// the bug this predicate exists to prevent.</para>
    ///
    /// <para>The condition below mirrors <c>AutorecoveringConnection.ShouldTriggerConnectionRecovery</c>
    /// in RabbitMQ.Client 7.2.1: recovery runs for a peer-initiated shutdown unless the broker
    /// refused access, and for a library-initiated one (an EOF from a lost node) unless the
    /// AppDomain is unloading. Anything else — notably an application-initiated close — is
    /// final, and only then is replacing the connection the right move. Mirroring the client's
    /// own predicate is deliberate: the alternative is guessing, and guessing here is what
    /// produced the bug.</para>
    /// </summary>
    private static bool IsStillOurs(IConnection connection)
    {
        if (connection.IsOpen)
        {
            return true;
        }

        // Null while the connection is being torn down but before a reason is recorded.
        // Treated as recovering, because "no reason yet" is not evidence of a terminal one.
        if (connection.CloseReason is not { } reason)
        {
            return true;
        }

        return reason.Initiator switch
        {
            ShutdownInitiator.Peer => reason.ReplyCode != Constants.AccessRefused,
            ShutdownInitiator.Library =>
                reason is not { Exception: ThreadAbortException, ReplyCode: Constants.InternalError },
            _ => false
        };
    }

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken ct)
    {
        var connection = await GetAsync(ct);

        // Confirmation tracking is what lets BasicPublishAsync await the broker's ack. The
        // outbox dispatcher depends on it; the tick sink would only be slowed by it.
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: publisherConfirms,
            publisherConfirmationTrackingEnabled: publisherConfirms);

        return await connection.CreateChannelAsync(channelOptions, ct);
    }

    private async Task<IConnection> OpenWithBackoffAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,

            // Reconnection after the first successful connect is the client's job, not
            // ours. This only covers the cold start.
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await factory.CreateConnectionAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "RabbitMQ connection failed; retrying in {Delay}.", delay);

                await Task.Delay(delay, ct);

                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2,
                             _options.MaxConnectionRetryDelay.TotalMilliseconds));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Interrupt any in-flight connection attempt — including one sitting in the retry
        // delay — so acquiring the gate below cannot block for the rest of that backoff.
        await _shutdownCts.CancelAsync();

        await _gate.WaitAsync();

        try
        {
            if (_connection is not null)
            {
                try
                {
                    await _connection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex, "Failed to dispose the RabbitMQ connection during shutdown.");
                }

                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        _shutdownCts.Dispose();
    }
}
