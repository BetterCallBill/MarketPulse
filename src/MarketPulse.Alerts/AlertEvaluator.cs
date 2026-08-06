using System.Text.Json;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Telemetry;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketPulse.Alerts;

/// <summary>
/// The one piece of business logic in the worker: given a tick, trigger whatever it crosses
/// and record the event to be published. Everything else here is transport.
/// </summary>
public sealed class AlertEvaluator(
    IAlertRuleRepository rules,
    IOutbox outbox,
    ILogger<AlertEvaluator> logger)
{
    /// <returns>How many rules this tick triggered.</returns>
    public async Task<int> EvaluateAsync(
        PriceTickMessage tick, string? correlationId, CancellationToken ct)
    {
        Telemetry.AlertsEvaluated.Add(1);

        // Queried per tick rather than cached. Four ticks a second against an index on
        // (Ticker, Status) is nothing; the spec names the in-memory cache as the remedy
        // when a real feed lands in slice 6.
        var candidates = await rules.GetActiveForTickerAsync(tick.Ticker, ct);

        var triggered = 0;

        foreach (var rule in candidates)
        {
            if (!rule.Evaluate(tick.Price))
            {
                continue;
            }

            rule.MarkTriggered(tick.Price, tick.TimestampUtc);

            var messageId = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new AlertTriggeredMessage(
                messageId,
                rule.Id,
                rule.UserId,
                rule.Ticker,
                rule.Direction.ToString(),
                rule.Threshold,
                tick.Price,
                tick.TimestampUtc));

            await outbox.EnqueueAsync(
                messageId, nameof(AlertTriggeredMessage), payload, correlationId,
                tick.TimestampUtc, ct);

            try
            {
                // One SaveChanges, one transaction: the rule's new status and the event
                // that announces it commit together or not at all. Saving per rule rather
                // than per tick keeps one lost race from discarding its siblings' work.
                await rules.SaveChangesAsync(ct);
                Telemetry.AlertsTriggered.Add(1);
                triggered++;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Another worker instance got there first. Expected under horizontal
                // scaling — not an error, and nothing was published.
                logger.LogDebug(
                    "Rule {RuleId} was triggered concurrently; discarding this evaluation.",
                    rule.Id);

                // Losing one race must cost exactly one rule, not the rest of the tick. The
                // tick is acked either way, so a rule abandoned here never sees this price
                // again: "IVV above 100" and "IVV above 105" both crossed by a tick at 106
                // would leave the second one armed against a market that has already moved.
                //
                // A bare `continue` is not enough, and that is why this is not a one-word
                // change. A failed SaveChanges rolls back the transaction but leaves every
                // entity it tried to write still tracked — the rule Modified against a
                // RowVersion the database no longer has, and the outbox row Added. The next
                // rule's SaveChanges would replay both, failing again on the rule and, worse,
                // inserting an event announcing an alert this instance did not win. Dropping
                // both from the unit of work is what makes continuing safe.
                foreach (var conflicted in ex.Entries)
                {
                    conflicted.State = EntityState.Detached;
                }

                outbox.Discard(messageId);
            }
        }

        return triggered;
    }
}
