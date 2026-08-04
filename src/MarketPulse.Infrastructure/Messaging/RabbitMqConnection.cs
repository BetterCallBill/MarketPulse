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
    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);

        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            _connection = await OpenWithBackoffAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
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
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
