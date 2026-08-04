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

    private async Task<Guid> SeedRuleAsync(string ticker, AlertDirection direction, decimal threshold)
    {
        await using var db = fixture.CreateContext();

        var user = User.Register($"eval-{Guid.NewGuid():N}@marketpulse.local", "hash");
        db.Users.Add(user);

        var rule = AlertRule.Create(user.Id, ticker, direction, threshold, Now);
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
