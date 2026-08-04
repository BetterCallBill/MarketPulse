using System.Net;
using System.Net.Sockets;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace MarketPulse.IntegrationTests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    // Docker Desktop does not reliably preserve an auto-assigned host port across a
    // `docker stop` / `docker start` cycle on the same container — observed here to hand
    // out a *different* ephemeral host port on restart, which would silently strand every
    // client's cached connection pointed at the old one forever (their automatic recovery
    // keeps retrying a port nothing listens on anymore). Binding a specific host port up
    // front — chosen once, the way a real deployment's port is fixed — keeps the chaos
    // test's "same container, same address" premise true regardless of that quirk.
    private readonly RabbitMqContainer _container =
        new RabbitMqBuilder("rabbitmq:3-management")
            .WithPortBinding(GetFreeTcpPort(), 5672)
            .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task<IConnection> ConnectAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(ConnectionString) };
        return await factory.CreateConnectionAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// The chaos test's kill switch. Stop/start (not dispose/recreate) keeps the same
    /// container, so the port mapping and the durable queues survive the outage — the
    /// broker "comes back" the way a restarted production node would.
    /// </summary>
    public Task StopBrokerAsync() => _container.StopAsync();

    public Task StartBrokerAsync() => _container.StartAsync();

    /// <summary>
    /// Asks the OS for an ephemeral port and immediately releases it, so the container can
    /// be bound to a concrete host port that is (almost certainly) free, instead of one
    /// Docker Desktop is free to reassign out from under a running test on restart.
    /// </summary>
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// Both containers in one collection so a test class that needs the API (SQL Server) and
/// the broker together gets them from a single fixture lifetime rather than starting a
/// second copy of each.
/// </summary>
[CollectionDefinition(nameof(MessagingCollection))]
public sealed class MessagingCollection
    : ICollectionFixture<SqlServerFixture>, ICollectionFixture<RabbitMqFixture>;
