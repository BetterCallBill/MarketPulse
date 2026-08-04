using System.Text.Json;
using MarketPulse.Application.Abstractions;
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
                triggered++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker instance got there first. Expected under horizontal
                // scaling — not an error, and nothing was published.
                logger.LogDebug(
                    "Rule {RuleId} was triggered concurrently; discarding this evaluation.",
                    rule.Id);
                return triggered;
            }
        }

        return triggered;
    }
}
