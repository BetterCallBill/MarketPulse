using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Domain.Entities;

public enum AlertDirection { Above, Below }

public enum AlertRuleStatus { Active, Triggered }

/// <summary>
/// One threshold an investor asked to be told about. One-shot: it fires once and stops,
/// until the owner re-arms it. See the slice 4a spec for why crossing detection and
/// cooldown windows were both rejected.
/// </summary>
public sealed class AlertRule
{
    /// <summary>
    /// Mirrors <see cref="Watchlist.MaxItems"/>. Enforced in the create handler against a
    /// count query, because a standalone rule cannot see its siblings.
    /// </summary>
    public const int MaxPerUser = 20;

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public AlertDirection Direction { get; private set; }
    public decimal Threshold { get; private set; }
    public AlertRuleStatus Status { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset? TriggeredUtc { get; private set; }
    public decimal? TriggeredPrice { get; private set; }

    /// <summary>
    /// SQL Server rowversion. Two worker instances compete on one queue, so two ticks can
    /// race the same rule; the loser's UPDATE matches no row and is discarded. This is what
    /// makes ADR-001's "scales independently" true rather than aspirational.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    private AlertRule() { }

    public static AlertRule Create(
        Guid userId,
        string ticker,
        AlertDirection direction,
        decimal threshold,
        DateTimeOffset now)
    {
        if (threshold <= 0)
        {
            throw new InvalidThresholdException();
        }

        return new AlertRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Ticker = ticker.Trim().ToUpperInvariant(),
            Direction = direction,
            Threshold = threshold,
            Status = AlertRuleStatus.Active,
            CreatedUtc = now
        };
    }

    public bool Evaluate(decimal price) =>
        Status == AlertRuleStatus.Active &&
        (Direction is AlertDirection.Above ? price >= Threshold : price <= Threshold);

    public void MarkTriggered(decimal price, DateTimeOffset now)
    {
        if (Status != AlertRuleStatus.Active)
        {
            throw new AlertNotActiveException();
        }

        Status = AlertRuleStatus.Triggered;
        TriggeredPrice = price;
        TriggeredUtc = now;
    }

    public void Rearm()
    {
        if (Status != AlertRuleStatus.Triggered)
        {
            throw new AlertNotTriggeredException();
        }

        Status = AlertRuleStatus.Active;
        TriggeredPrice = null;
        TriggeredUtc = null;
    }
}
