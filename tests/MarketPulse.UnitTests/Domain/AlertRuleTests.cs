using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.UnitTests.Domain;

public class AlertRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AlertRule Rule(AlertDirection direction, decimal threshold) =>
        AlertRule.Create(Guid.NewGuid(), "IVV", direction, threshold, Now);

    // The truth table. Evaluate is a pure function of (rule, price) with no prior-tick
    // memory, which is the whole reason it can be tested like this.
    [Theory]
    [InlineData(AlertDirection.Above, 50.00, 50.01, true)]
    [InlineData(AlertDirection.Above, 50.00, 50.00, true)]   // boundary is inclusive
    [InlineData(AlertDirection.Above, 50.00, 49.99, false)]
    [InlineData(AlertDirection.Below, 50.00, 49.99, true)]
    [InlineData(AlertDirection.Below, 50.00, 50.00, true)]   // boundary is inclusive
    [InlineData(AlertDirection.Below, 50.00, 50.01, false)]
    public void Evaluate_answers_the_truth_table(
        AlertDirection direction, decimal threshold, decimal price, bool expected)
    {
        Assert.Equal(expected, Rule(direction, threshold).Evaluate(price));
    }

    [Fact]
    public void An_already_triggered_rule_never_evaluates_true_again()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(55m, Now);

        // One-shot semantics: the price is still well above the threshold, and that is
        // exactly the case that must not fire a second time.
        Assert.False(rule.Evaluate(55m));
    }

    [Fact]
    public void A_new_rule_is_active_and_carries_no_trigger_details()
    {
        var rule = Rule(AlertDirection.Above, 50m);

        Assert.Equal(AlertRuleStatus.Active, rule.Status);
        Assert.Null(rule.TriggeredUtc);
        Assert.Null(rule.TriggeredPrice);
        Assert.Equal(Now, rule.CreatedUtc);
    }

    [Fact]
    public void MarkTriggered_records_the_price_and_the_time()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(51.25m, Now);

        Assert.Equal(AlertRuleStatus.Triggered, rule.Status);
        Assert.Equal(51.25m, rule.TriggeredPrice);
        Assert.Equal(Now, rule.TriggeredUtc);
    }

    [Fact]
    public void MarkTriggered_on_an_already_triggered_rule_is_rejected()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(51m, Now);

        Assert.Throws<AlertNotActiveException>(() => rule.MarkTriggered(52m, Now));
    }

    [Fact]
    public void Rearm_returns_a_triggered_rule_to_active_and_clears_the_details()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(51m, Now);
        rule.Rearm();

        Assert.Equal(AlertRuleStatus.Active, rule.Status);
        Assert.Null(rule.TriggeredUtc);
        Assert.Null(rule.TriggeredPrice);
        Assert.True(rule.Evaluate(51m));
    }

    [Fact]
    public void Rearm_on_an_active_rule_is_rejected()
    {
        Assert.Throws<AlertNotTriggeredException>(() => Rule(AlertDirection.Above, 50m).Rearm());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_of_zero_or_less_is_rejected(decimal threshold)
    {
        Assert.Throws<InvalidThresholdException>(() => Rule(AlertDirection.Above, threshold));
    }

    [Fact]
    public void The_ticker_is_normalised_the_way_watchlist_items_are()
    {
        var rule = AlertRule.Create(Guid.NewGuid(), "  ivv ", AlertDirection.Above, 50m, Now);

        Assert.Equal("IVV", rule.Ticker);
    }

    [Fact]
    public void The_per_user_limit_is_twenty()
    {
        // Enforced in the create handler, not here — an AlertRule cannot see its siblings.
        // Asserted so the number cannot drift away from the spec unnoticed.
        Assert.Equal(20, AlertRule.MaxPerUser);
    }
}
