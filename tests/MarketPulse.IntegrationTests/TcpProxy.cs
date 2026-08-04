using System.Net;
using System.Net.Sockets;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// A loopback TCP forwarder that can be cut and reconnected on demand, sitting between a
/// client and the shared RabbitMQ container.
///
/// <para>Used instead of stopping and starting the container, for three reasons. It is
/// deterministic — <see cref="Sever"/> closes every live socket immediately, rather than
/// waiting on Docker and a broker's shutdown sequence. It leaves the broker itself untouched,
/// so the queues, exchanges and messages of every other class sharing
/// <see cref="MessagingCollection"/> survive a test that simulates an outage. And it is fast
/// enough to run in the normal suite rather than being something nobody ever runs. From the
/// client's side it is indistinguishable from the real thing: an abruptly closed socket is
/// exactly what a broker restart looks like — an EOF, which the client reports as a
/// library-initiated shutdown and then tries to recover from.</para>
/// </summary>
internal sealed class TcpProxy : IAsyncDisposable
{
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _live = [];

    private volatile bool _severed;

    public TcpProxy(string upstreamHost, int upstreamPort)
    {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = AcceptLoopAsync();
    }

    /// <summary>The loopback port a client should connect to instead of the broker's.</summary>
    public int Port { get; }

    /// <summary>
    /// Drops every live connection and refuses new ones. The listener itself stays bound, so
    /// <see cref="Restore"/> cannot lose the port to something else in the meantime.
    /// </summary>
    public void Sever()
    {
        _severed = true;
        CloseLive();
    }

    public void Restore() => _severed = false;

    private void CloseLive()
    {
        lock (_live)
        {
            foreach (var client in _live)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    // Already gone. Nothing here is an assertion.
                }
            }

            _live.Clear();
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient downstream;

            try
            {
                downstream = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            if (_severed)
            {
                // Accept then close: the client sees the connection die during the AMQP
                // handshake, which is what it sees against a broker that is still booting.
                downstream.Close();
                continue;
            }

            Track(downstream);
            _ = PumpAsync(downstream);
        }
    }

    private void Track(TcpClient client)
    {
        lock (_live)
        {
            _live.Add(client);
        }
    }

    private async Task PumpAsync(TcpClient downstream)
    {
        TcpClient? upstream = null;

        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync(_upstreamHost, _upstreamPort, _cts.Token);
            Track(upstream);

            var toBroker = downstream.GetStream().CopyToAsync(upstream.GetStream(), _cts.Token);
            var toClient = upstream.GetStream().CopyToAsync(downstream.GetStream(), _cts.Token);

            await Task.WhenAny(toBroker, toClient);
        }
        catch
        {
            // Either end going away is the normal way this method finishes.
        }
        finally
        {
            try
            {
                downstream.Close();
                upstream?.Close();
            }
            catch
            {
                // Already gone.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        CloseLive();
        _listener.Stop();
        _cts.Dispose();
    }
}
