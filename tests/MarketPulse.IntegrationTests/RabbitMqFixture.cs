using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace MarketPulse.IntegrationTests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container =
        new RabbitMqBuilder("rabbitmq:3-management").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task<IConnection> ConnectAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(ConnectionString) };
        return await factory.CreateConnectionAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// Both containers in one collection so a test class that needs the API (SQL Server) and
/// the broker together gets them from a single fixture lifetime rather than starting a
/// second copy of each.
/// </summary>
[CollectionDefinition(nameof(MessagingCollection))]
public sealed class MessagingCollection
    : ICollectionFixture<SqlServerFixture>, ICollectionFixture<RabbitMqFixture>;
