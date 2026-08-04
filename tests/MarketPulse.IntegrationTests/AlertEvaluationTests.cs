using MarketPulse.Alerts;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure;
using MarketPulse.Infrastructure.Messaging.Contracts;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(MessagingCollection))]
public class AlertEvaluationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds the evaluator over a real DbContext, the way the worker's DI does. Scoped
    /// lifetimes matter here: the evaluator, the repository and the outbox must share one
    /// context or the "one transaction" claim is false.
    /// </summary>
    private ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddPersistence(fixture.ConnectionString)
            // Registered against ILogger<T>, not the concrete NullLogger<T>: the evaluator
            // asks for the interface, and DI matches on the exact service type.
            .AddSingleton<ILogger<AlertEvaluator>>(NullLogger<AlertEvaluator>.Instance)
            .AddScoped<AlertEvaluator>()
            .BuildServiceProvider();

    private async Task<Guid> SeedRuleAsync(
        string ticker, AlertDirection direction, decimal threshold, DateTimeOffset? createdUtc = null)
    {
        await using var db = fixture.CreateContext();

        var user = User.Register($"eval-{Guid.NewGuid():N}@marketpulse.local", "hash");
        db.Users.Add(user);

        var rule = AlertRule.Create(user.Id, ticker, direction, threshold, createdUtc ?? Now);
        db.AlertRules.Add(rule);

        await db.SaveChangesAsync();
        return rule.Id;
    }

    [Fact]
    public async Task A_crossing_tick_triggers_the_rule_and_writes_one_outbox_row()
    {
        var ruleId = await SeedRuleAsync("IVV", AlertDirection.Above, 50m);

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();

        var triggered = await evaluator.EvaluateAsync(
            new PriceTickMessage("IVV", 51m, Now), "corr-1", CancellationToken.None);

        Assert.Equal(1, triggered);

        await using var db = fixture.CreateContext();

        var rule = await db.AlertRules.SingleAsync(r => r.Id == ruleId);
        Assert.Equal(AlertRuleStatus.Triggered, rule.Status);
        Assert.Equal(51m, rule.TriggeredPrice);

        var outbox = await db.OutboxMessages
            .Where(m => m.CorrelationId == "corr-1")
            .SingleAsync();

        Assert.Equal(nameof(AlertTriggeredMessage), outbox.Type);
        Assert.Null(outbox.DispatchedUtc);
        Assert.Contains("\"Ticker\":\"IVV\"", outbox.Payload);
    }

    [Fact]
    public async Task A_tick_that_does_not_cross_writes_nothing()
    {
        var ruleId = await SeedRuleAsync("NDQ", AlertDirection.Above, 50m);

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();

        var triggered = await evaluator.EvaluateAsync(
            new PriceTickMessage("NDQ", 49m, Now), "corr-2", CancellationToken.None);

        Assert.Equal(0, triggered);

        await using var db = fixture.CreateContext();
        Assert.Equal(
            AlertRuleStatus.Active,
            (await db.AlertRules.SingleAsync(r => r.Id == ruleId)).Status);
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.CorrelationId == "corr-2"));
    }

    [Fact]
    public async Task A_second_tick_does_not_trigger_an_already_triggered_rule()
    {
        await SeedRuleAsync("VHY", AlertDirection.Above, 50m);

        await using var provider = BuildProvider();

        async Task<int> EvaluateAsync(string correlationId)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<AlertEvaluator>()
                .EvaluateAsync(
                    new PriceTickMessage("VHY", 51m, Now), correlationId, CancellationToken.None);
        }

        Assert.Equal(1, await EvaluateAsync("corr-3a"));

        // One-shot. The price is still above the threshold, which is exactly the case that
        // must not fire again.
        Assert.Equal(0, await EvaluateAsync("corr-3b"));

        await using var db = fixture.CreateContext();
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.CorrelationId == "corr-3b"));
    }

    /// <summary>
    /// Two rules on one ticker, both crossed by one tick, with the first losing a forced
    /// <c>RowVersion</c> race. <c>Two_concurrent_evaluations_of_one_rule_produce_one_outbox_row</c>
    /// cannot see this: one rule has no siblings to abandon.
    /// </summary>
    [Fact]
    public async Task A_rule_that_loses_a_concurrency_race_leaves_its_siblings_evaluated_and_publishes_nothing()
    {
        // A ticker of this test's own. The database is shared across MessagingCollection and
        // never reset, and this test asserts on how many rules one tick triggered — a number
        // any leftover Active rule on a shared ticker would change. AlertRules has no foreign
        // key to the reference table, so the evaluator is happy to be asked about one.
        const string ticker = "RACE";
        var correlationId = $"corr-race-{Guid.NewGuid():N}";

        // GetActiveForTickerAsync orders by CreatedUtc, so "loser" is reached first and the
        // question "was the second rule still evaluated?" has a determinate answer.
        var loserId = await SeedRuleAsync(ticker, AlertDirection.Above, 100m, Now);
        var survivorId = await SeedRuleAsync(
            ticker, AlertDirection.Above, 105m, Now.AddMinutes(1));

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketPulseDbContext>();

        // Load the loser into the evaluator's own context first, so the RowVersion it will
        // save against is captured now...
        _ = await db.AlertRules.SingleAsync(r => r.Id == loserId);

        // ...and then move the row underneath it, exactly as a second worker instance would.
        // Written through a separate context and left Active, so the evaluator's query still
        // returns it — and returns the stale tracked instance, which is what makes the save
        // lose.
        await using (var other = fixture.CreateContext())
        {
            await other.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE AlertRules SET TriggeredPrice = 1 WHERE Id = {loserId}");
        }

        var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();

        var triggered = await evaluator.EvaluateAsync(
            new PriceTickMessage(ticker, 106m, Now), correlationId, CancellationToken.None);

        Assert.Equal(1, triggered);

        await using var verify = fixture.CreateContext();

        // The rule this instance lost is untouched — the other worker owns it now.
        Assert.Equal(
            AlertRuleStatus.Active,
            (await verify.AlertRules.SingleAsync(r => r.Id == loserId)).Status);

        // And the sibling was still evaluated. PriceConsumer acks the tick either way, so a
        // rule abandoned here never sees this price again.
        var survivor = await verify.AlertRules.SingleAsync(r => r.Id == survivorId);
        Assert.Equal(AlertRuleStatus.Triggered, survivor.Status);
        Assert.Equal(106m, survivor.TriggeredPrice);

        // Exactly one event, for the rule that was actually won. Two would mean the loser's
        // outbox row — still tracked as Added after its own save rolled back — had been
        // carried to the broker by the survivor's save, announcing an alert this instance
        // never triggered.
        var published = await verify.OutboxMessages
            .Where(m => m.CorrelationId == correlationId)
            .ToListAsync();

        Assert.Single(published);
        Assert.Contains(survivorId.ToString(), published[0].Payload);
    }

    [Fact]
    public async Task Two_concurrent_evaluations_of_one_rule_produce_one_outbox_row()
    {
        await SeedRuleAsync("FANG", AlertDirection.Below, 50m);

        await using var provider = BuildProvider();

        async Task<int> EvaluateAsync(string correlationId)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<AlertEvaluator>()
                .EvaluateAsync(
                    new PriceTickMessage("FANG", 49m, Now), correlationId, CancellationToken.None);
        }

        // Two worker instances, two ticks, one rule. The RowVersion loser must lose
        // silently rather than double-notifying.
        var results = await Task.WhenAll(EvaluateAsync("corr-4a"), EvaluateAsync("corr-4b"));

        Assert.Equal(1, results.Sum());

        await using var db = fixture.CreateContext();
        Assert.Equal(
            1,
            await db.OutboxMessages.CountAsync(
                m => m.CorrelationId == "corr-4a" || m.CorrelationId == "corr-4b"));
    }
}
