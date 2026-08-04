using MarketPulse.Alerts;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(MessagingCollection))]
public class OutboxDispatchTests(SqlServerFixture sql, RabbitMqFixture rabbit)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private ServiceProvider BuildProvider()
    {
        var uri = new Uri(rabbit.ConnectionString);

        var options = new RabbitMqOptions
        {
            HostName = uri.Host,
            Port = uri.Port,
            UserName = uri.UserInfo.Split(':')[0],
            Password = uri.UserInfo.Split(':')[1]
        };

        return new ServiceCollection()
            .AddPersistence(sql.ConnectionString)
            .AddSingleton<IOptions<RabbitMqOptions>>(Options.Create(options))
            .AddLogging()
            .AddMessaging()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task A_pending_row_is_published_and_marked_dispatched()
    {
        var messageId = Guid.NewGuid();

        await using (var db = sql.CreateContext())
        {
            db.OutboxMessages.Add(OutboxMessage.Create(
                messageId, "AlertTriggeredMessage", """{"Ticker":"IVV"}""", "corr-out", Now));
            await db.SaveChangesAsync();
        }

        await using var provider = BuildProvider();
        var options = provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        // Drain anything a previous test left behind so the assertion below is about us.
        await using (var connection = await rabbit.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
            await channel.QueuePurgeAsync(options.NotificationsQueue);
        }

        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);

        var dispatched = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.True(dispatched >= 1);

        await using (var db = sql.CreateContext())
        {
            Assert.NotNull(
                (await db.OutboxMessages.SingleAsync(m => m.Id == messageId)).DispatchedUtc);
        }

        await using (var connection = await rabbit.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            var delivered = await channel.BasicGetAsync(
                options.NotificationsQueue, autoAck: true);

            Assert.NotNull(delivered);
            Assert.Equal("corr-out", delivered!.BasicProperties.CorrelationId);
        }
    }
}
