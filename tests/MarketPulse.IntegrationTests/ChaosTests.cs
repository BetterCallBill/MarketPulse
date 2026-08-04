using System.Net;
using System.Net.Http.Json;
using MarketPulse.Alerts;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// The README's headline claim, demonstrated rather than asserted: an alert that fires
/// while RabbitMQ is down is not lost. Real API host (rule CRUD, tick feed into the
/// broker, AlertTriggeredConsumer writing notification rows), real worker services
/// (PriceConsumer + AlertEvaluator hosted exactly as MarketPulse.Alerts' Program.cs
/// hosts them), and a real broker container stopped mid-flow.
///
/// <para>Outbox dispatch is driven by hand through the same public seam
/// OutboxDispatchTests uses. That is what makes the interesting window — rule marked
/// Triggered, outbox row committed, nothing dispatched yet — a place this test stands
/// still in, rather than a 500 ms slot it races the hosted dispatcher for.</para>
/// </summary>
[Collection(nameof(MessagingCollection))]
public class ChaosTests(SqlServerFixture sql, RabbitMqFixture rabbit)
{
    private sealed record AlertRuleResponse(Guid Id, string Ticker, string Status);

    private sealed record NotificationResponse(Guid Id, string Ticker, decimal TriggeredPrice);

    private Dictionary<string, string?> BrokerSettings()
    {
        var uri = new Uri(rabbit.ConnectionString);

        return new Dictionary<string, string?>
        {
            ["RabbitMq:HostName"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:UserName"] = uri.UserInfo.Split(':')[0],
            ["RabbitMq:Password"] = uri.UserInfo.Split(':')[1],

            // Keeps every reconnect loop brisk after the restart. The production cap of
            // 30s is for real outages; this test is not asserting anything about backoff.
            ["RabbitMq:MaxConnectionRetryDelay"] = "00:00:02"
        };
    }

    /// <summary>The worker composed as Program.cs composes it, minus the hosted dispatcher.</summary>
    private IHost BuildWorker()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(BrokerSettings());

        builder.Services.AddOptions<RabbitMqOptions>()
            .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName));

        builder.Services.AddPersistence(sql.ConnectionString);
        builder.Services.AddMessaging();
        builder.Services.AddScoped<AlertEvaluator>();
        builder.Services.AddHostedService<PriceConsumer>();

        return builder.Build();
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, string what, int seconds = 60)
    {
        for (var i = 0; i < seconds * 2; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail($"Timed out after {seconds}s waiting for {what}.");
    }

    /// <summary>One dispatch attempt, bounded so a wedged broker call cannot hang the test.</summary>
    private static async Task<int> TryDispatchAsync(OutboxDispatcher dispatcher)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            return await dispatcher.DispatchPendingAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // A broker that is down answers with whatever exception it likes; every one
            // of them means "nothing was confirmed", which is all the caller needs.
            return 0;
        }
    }

    [Fact]
    public async Task An_alert_that_fires_while_the_broker_is_down_is_delivered_exactly_once_after_it_returns()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());
        var client = await AuthenticatedClient.RegisterAsync(factory);

        using var worker = BuildWorker();
        await worker.StartAsync();

        try
        {
            // An Above rule at one cent: the API's fake feed prices all sit far above it,
            // so the very next tick through the real broker trips the rule. One-shot
            // semantics mean exactly one trigger no matter how many ticks follow.
            var created = await client.PostAsJsonAsync(
                "/api/v1/alerts",
                new { Ticker = "IVV", Direction = "Above", Threshold = 0.01m });
            var rule = await created.Content.ReadFromJsonAsync<AlertRuleResponse>();
            Assert.NotNull(rule);

            // The rule flip and its outbox row commit in one unit of work, so "Triggered"
            // means the row is already there — and with no dispatcher hosted, it stays
            // pending for as long as this test likes. The window is now open.
            await WaitUntilAsync(async () =>
            {
                await using var db = sql.CreateContext();
                return await db.AlertRules
                    .AnyAsync(r => r.Id == rule!.Id && r.Status == AlertRuleStatus.Triggered);
            }, "the rule to trigger off the live feed");

            Guid pendingId;
            await using (var db = sql.CreateContext())
            {
                pendingId = await db.OutboxMessages
                    .Where(m => m.DispatchedUtc == null
                                && m.Payload.Contains(rule!.Id.ToString()))
                    .Select(m => m.Id)
                    .SingleAsync();
            }

            // ---- The outage. ----
            await rabbit.StopBrokerAsync();

            var dispatcher = new OutboxDispatcher(
                worker.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OutboxDispatcher>.Instance);

            // Dispatch attempted against a dead broker: nothing is confirmed, nothing is
            // marked dispatched, and nothing crashes.
            Assert.Equal(0, await TryDispatchAsync(dispatcher));

            await using (var db = sql.CreateContext())
            {
                var stillPending = await db.OutboxMessages
                    .SingleAsync(m => m.Id == pendingId);
                Assert.Null(stillPending.DispatchedUtc);

                Assert.Equal(0, await db.Notifications.CountAsync(
                    n => n.AlertRuleId == rule!.Id));
            }

            // The API is alive throughout — the broker being down must not take it down.
            Assert.Equal(
                HttpStatusCode.OK,
                (await factory.CreateClient().GetAsync("/health")).StatusCode);

            // ---- The broker returns. ----
            await rabbit.StartBrokerAsync();

            // Connection recovery is the client library's job and takes a few seconds;
            // retry dispatch until exactly one row is confirmed through.
            var dispatched = 0;
            await WaitUntilAsync(async () =>
            {
                dispatched += await TryDispatchAsync(dispatcher);
                return dispatched >= 1;
            }, "the outbox row to dispatch after the broker returned");

            Assert.Equal(1, dispatched);

            // And the API's consumer — which lost its channel in the outage and
            // resubscribed through its supervision loop — lands the row exactly once.
            await WaitUntilAsync(async () =>
            {
                var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
                    "/api/v1/notifications");
                return list?.Count == 1;
            }, "the notification row to arrive via the API's consumer");

            // Grace period so a wrongly duplicated delivery has time to land and fail this.
            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.Single((await client.GetFromJsonAsync<List<NotificationResponse>>(
                "/api/v1/notifications"))!);

            await using (var finalDb = sql.CreateContext())
            {
                var row = await finalDb.OutboxMessages.SingleAsync(m => m.Id == pendingId);
                Assert.NotNull(row.DispatchedUtc);
            }
        }
        finally
        {
            // The broker must be back before the next class in this collection runs,
            // even when an assertion above has already failed.
            await rabbit.StartBrokerAsync();

            var stop = worker.StopAsync();
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15))));
        }
    }
}
