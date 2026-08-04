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

        // Sharper than a plain ">= 1" would be nice, but this database is shared with
        // AlertEvaluationTests (MessagingCollection, never reset between classes), which
        // permanently leaves a handful of undispatched rows behind. Those are legitimately
        // pending too, so a correct dispatcher sweeps them up in the same batch as ours —
        // the exact count is not under this test's control and asserting a specific number
        // would just be asserting the leftovers of an unrelated test class.
        Assert.True(dispatched >= 1);

        await using (var db = sql.CreateContext())
        {
            Assert.NotNull(
                (await db.OutboxMessages.SingleAsync(m => m.Id == messageId)).DispatchedUtc);
        }

        await using (var connection = await rabbit.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            // The queue can also contain the stale rows' messages, published in whatever
            // order OrderBy(OccurredUtc) happened to yield among same-timestamp rows — not
            // guaranteed to put ours first. Drain until we find our own message by MessageId
            // (the stronger key: it's the client-supplied GUID we inserted, not a
            // caller-controlled string like CorrelationId), skipping anything that isn't
            // ours. Bounded so a genuine failure fails instead of hanging.
            var found = await FindOwnMessageAsync(channel, options.NotificationsQueue, messageId);

            Assert.NotNull(found);
            Assert.Equal("corr-out", found!.BasicProperties.CorrelationId);
        }
    }

    /// <summary>
    /// Reads messages off the queue one at a time, discarding any that aren't ours, until our
    /// message id turns up or the queue runs dry. The dispatcher's batch size is 100, so no
    /// single dispatch pass can ever put more than 100 messages on this queue — the bound here
    /// is comfortably above that so a real match is never missed, while still terminating
    /// deterministically if our message is somehow never published at all.
    /// </summary>
    private static async Task<BasicGetResult?> FindOwnMessageAsync(
        IChannel channel, string queue, Guid messageId, int maxAttempts = 150)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var delivered = await channel.BasicGetAsync(queue, autoAck: true);

            if (delivered is null)
            {
                return null; // Queue drained; our message was never on it.
            }

            if (delivered.BasicProperties.MessageId == messageId.ToString())
            {
                return delivered;
            }

            // Someone else's leftover row — expected, not asserted on.
        }

        return null;
    }
}
