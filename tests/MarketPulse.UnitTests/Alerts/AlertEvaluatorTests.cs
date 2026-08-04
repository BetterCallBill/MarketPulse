using MarketPulse.Alerts;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MarketPulse.UnitTests.Alerts;

/// <summary>
/// The evaluation loop's behaviour when one rule in a tick fails. The integration suite
/// covers the transactional half against a real database; what needs a substitute is the
/// <em>order</em> of events, because "the first rule loses the race" has to be arranged
/// rather than raced for if the assertion is to mean anything.
/// </summary>
public class AlertEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task A_rule_that_loses_a_concurrency_race_does_not_stop_its_siblings_being_evaluated()
    {
        // "IVV above 100" and "IVV above 105", one tick at 106. Both crossed. If losing the
        // race on the first abandons the tick, the second is never evaluated — and because
        // PriceConsumer acks the tick either way, it never sees this price again. A tick at
        // 104 next second then leaves a genuinely crossed threshold silently unfired.
        var lost = AlertRule.Create(UserId, "IVV", AlertDirection.Above, 100m, Now);
        var survivor = AlertRule.Create(UserId, "IVV", AlertDirection.Above, 105m, Now);

        var rules = Substitute.For<IAlertRuleRepository>();
        rules.GetActiveForTickerAsync("IVV", Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<AlertRule>>(_ => [lost, survivor]);

        var saves = 0;
        rules.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Interlocked.Increment(ref saves) == 1
                ? Task.FromException(new DbUpdateConcurrencyException("another worker won"))
                : Task.CompletedTask);

        var outbox = Substitute.For<IOutbox>();
        var enqueued = new List<Guid>();

        outbox.EnqueueAsync(
                Arg.Do<Guid>(enqueued.Add), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        var evaluator = new AlertEvaluator(
            rules, outbox, NullLogger<AlertEvaluator>.Instance);

        var triggered = await evaluator.EvaluateAsync(
            new PriceTickMessage("IVV", 106m, Now), "corr", CancellationToken.None);

        // One save won, one lost — but both rules were reached.
        Assert.Equal(1, triggered);
        Assert.Equal(2, saves);
        Assert.Equal(2, enqueued.Count);

        Assert.Equal(AlertRuleStatus.Triggered, survivor.Status);
        Assert.Equal(106m, survivor.TriggeredPrice);

        // And the event for the rule this instance did not win is taken back off the unit of
        // work, so the survivor's SaveChanges cannot carry it to the broker.
        outbox.Received(1).Discard(enqueued[0]);
        outbox.DidNotReceive().Discard(enqueued[1]);
    }

    [Fact]
    public async Task Every_rule_a_tick_crosses_is_triggered()
    {
        // The control for the test above: with nobody losing a race, both rules fire. Without
        // it, "the second rule was evaluated" could pass for the wrong reason.
        var first = AlertRule.Create(UserId, "IVV", AlertDirection.Above, 100m, Now);
        var second = AlertRule.Create(UserId, "IVV", AlertDirection.Above, 105m, Now);

        var rules = Substitute.For<IAlertRuleRepository>();
        rules.GetActiveForTickerAsync("IVV", Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<AlertRule>>(_ => [first, second]);
        rules.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var outbox = Substitute.For<IOutbox>();

        var evaluator = new AlertEvaluator(
            rules, outbox, NullLogger<AlertEvaluator>.Instance);

        var triggered = await evaluator.EvaluateAsync(
            new PriceTickMessage("IVV", 106m, Now), "corr", CancellationToken.None);

        Assert.Equal(2, triggered);
        Assert.Equal(AlertRuleStatus.Triggered, first.Status);
        Assert.Equal(AlertRuleStatus.Triggered, second.Status);
        outbox.DidNotReceive().Discard(Arg.Any<Guid>());
    }
}
