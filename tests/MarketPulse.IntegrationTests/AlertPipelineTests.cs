using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// The slice's proof. An alert rule created over HTTP, a tick published to the broker, and a
/// notification arriving on a websocket — through the real worker code, the real outbox, and
/// a real RabbitMQ.
///
/// This class shares its database and broker with the rest of <see cref="MessagingCollection"/>
/// (never reset between classes — see Task 8's fix-round note on <c>OutboxDispatchTests</c>).
/// Every user this class asserts against is a freshly registered random Guid it creates
/// itself, so the notifications API — already scoped to the calling user — cannot surface
/// another class's residue for that user. The one genuinely global, unscoped resource is the
/// dead-letter queue, where a marker match (not queue position) is used instead.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class AlertPipelineTests(SqlServerFixture sql, RabbitMqFixture rabbit)
{
    private sealed record NotificationResponse(Guid Id, string Ticker, decimal TriggeredPrice);

    private sealed record NotificationPush(Guid Id, string Ticker, decimal TriggeredPrice);

    private Dictionary<string, string?> BrokerSettings()
    {
        var uri = new Uri(rabbit.ConnectionString);

        return new Dictionary<string, string?>
        {
            ["RabbitMq:HostName"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:UserName"] = uri.UserInfo.Split(':')[0],
            ["RabbitMq:Password"] = uri.UserInfo.Split(':')[1]
        };
    }

    private static async Task PublishAlertAsync(
        RabbitMqFixture rabbit, RabbitMqOptions options, AlertTriggeredMessage message)
    {
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);

        await channel.BasicPublishAsync(
            exchange: options.AlertsExchange,
            routingKey: RabbitMqTopology.AlertTriggeredRoutingKey,
            mandatory: false,
            basicProperties: new BasicProperties
            {
                Persistent = true,
                MessageId = message.MessageId.ToString(),
                CorrelationId = "pipeline-test"
            },
            body: JsonSerializer.SerializeToUtf8Bytes(message),
            cancellationToken: CancellationToken.None);
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe, int attempts = 50)
        where T : class
    {
        for (var i = 0; i < attempts; i++)
        {
            var result = await probe();

            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100);
        }

        return null;
    }

    [Fact]
    public async Task An_alert_message_becomes_a_notification_and_reaches_only_its_owner()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());

        var aliceEmail = AuthenticatedClient.NewEmail();
        var alice = await AuthenticatedClient.RegisterAsync(factory, aliceEmail);
        var bob = await AuthenticatedClient.RegisterAsync(factory);

        Guid aliceId;
        await using (var db = sql.CreateContext())
        {
            aliceId = await db.Users.Where(u => u.Email == aliceEmail)
                                    .Select(u => u.Id).SingleAsync();
        }

        var options = factory.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        var accessCookie = AuthenticatedClient.ReadCookie(
            await factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/login",
                new { Email = aliceEmail, Password = AuthenticatedClient.ValidPassword }),
            "mp_access");

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = $"mp_access={accessCookie}";
            })
            .Build();

        var pushed = new TaskCompletionSource<NotificationPush>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Clients.User(aliceId) already means only Alice's connection ever sees this push
        // (per-user delivery is the property under test), so any "notification" event that
        // reaches this connection is necessarily about her — no further filtering needed.
        connection.On<NotificationPush>("notification", p => pushed.TrySetResult(p));
        await connection.StartAsync();

        var message = new AlertTriggeredMessage(
            Guid.NewGuid(), Guid.NewGuid(), aliceId, "IVV",
            "Above", 50m, 51.5m, DateTimeOffset.UtcNow);

        await PublishAlertAsync(rabbit, options, message);

        var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        await connection.DisposeAsync();

        Assert.Same(pushed.Task, completed);
        Assert.Equal("IVV", (await pushed.Task).Ticker);

        // Persisted, and visible over HTTP. Alice is a user this test just created, so her
        // notification list can only ever contain rows this test itself caused to be
        // written — no other test writes notifications for a random Guid it never saw.
        var stored = await WaitForAsync(async () =>
        {
            var list = await alice.GetFromJsonAsync<List<NotificationResponse>>(
                "/api/v1/notifications");
            return list?.Count > 0 ? list : null;
        });

        Assert.NotNull(stored);
        Assert.Equal(51.5m, stored![0].TriggeredPrice);

        // And Bob — also freshly created, also empty by construction — sees nothing.
        // Prices are public; notifications are not.
        Assert.Empty((await bob.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications"))!);
    }

    [Fact]
    public async Task Redelivering_the_same_message_id_yields_exactly_one_notification()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());

        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(factory, email);

        Guid userId;
        await using (var db = sql.CreateContext())
        {
            userId = await db.Users.Where(u => u.Email == email)
                                   .Select(u => u.Id).SingleAsync();
        }

        var options = factory.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        var message = new AlertTriggeredMessage(
            Guid.NewGuid(), Guid.NewGuid(), userId, "NDQ",
            "Below", 40m, 39m, DateTimeOffset.UtcNow);

        // The same message twice, exactly as the outbox would republish after a lost confirm.
        await PublishAlertAsync(rabbit, options, message);
        await PublishAlertAsync(rabbit, options, message);

        var stored = await WaitForAsync(async () =>
        {
            var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
                "/api/v1/notifications");
            return list?.Count > 0 ? list : null;
        });

        Assert.NotNull(stored);

        // Give the second delivery time to be wrongly inserted, so this test can fail.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var final = await client.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications");

        // This user was registered fresh by this test, so its notification list belongs to
        // this test alone: a single row proves the second delivery was deduplicated, not
        // merely that our own row is present somewhere in a longer list.
        Assert.Single(final!);
    }

    [Fact]
    public async Task An_unparseable_message_reaches_the_dead_letter_queue()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());
        _ = await AuthenticatedClient.RegisterAsync(factory);

        var options = factory.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);

        // The dead-letter queue is the one resource in this class that is not scoped to a
        // user this test created — it is global, and other classes/messages in this shared
        // broker can land on it too. A marker on the message we publish, checked on
        // whatever we dequeue, is what makes the assertion about our own message rather
        // than "the first thing sitting in the queue".
        var marker = Guid.NewGuid().ToString();

        await channel.BasicPublishAsync(
            exchange: options.AlertsExchange,
            routingKey: RabbitMqTopology.AlertTriggeredRoutingKey,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true, CorrelationId = marker },
            body: Encoding.UTF8.GetBytes("this is not json"),
            cancellationToken: CancellationToken.None);

        var deadLettered = await FindOwnDeadLetterAsync(
            channel, options.NotificationsDeadLetterQueue, marker);

        // One bad message must not block the queue behind it, and the message found must be
        // the one this test published, not a stray leftover from another test class.
        Assert.NotNull(deadLettered);
    }

    /// <summary>
    /// Drains the dead-letter queue one message at a time, discarding anything that is not
    /// ours, until our own marker turns up or the queue runs dry. Bounded so a genuine
    /// failure (our message was never dead-lettered at all) fails the test instead of
    /// hanging. Mirrors the drain-and-search pattern <c>OutboxDispatchTests</c> uses for the
    /// same reason: a shared queue with no guaranteed ordering across test classes.
    /// </summary>
    private static async Task<BasicGetResult?> FindOwnDeadLetterAsync(
        IChannel channel, string queue, string marker, int maxPolls = 100)
    {
        // Two nested loops on purpose: the inner one drains whatever is currently on the
        // queue as fast as possible (stale messages need no delay between them), and the
        // outer one waits out the time it takes our own message to travel from "just
        // published" to "nacked and dead-lettered" by the consumer, which has not
        // necessarily happened yet on the first pass.
        for (var poll = 0; poll < maxPolls; poll++)
        {
            while (true)
            {
                var delivered = await channel.BasicGetAsync(queue, autoAck: true);

                if (delivered is null)
                {
                    break;
                }

                if (delivered.BasicProperties.CorrelationId == marker)
                {
                    return delivered;
                }

                // Someone else's dead letter — expected, not asserted on.
            }

            await Task.Delay(100);
        }

        return null;
    }
}
