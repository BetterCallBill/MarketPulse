namespace MarketPulse.Domain.Entities;

/// <summary>
/// A triggered alert, delivered. The rule's details are snapshotted rather than joined, so
/// deleting a rule does not rewrite the user's history.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; private set; }

    /// <summary>
    /// The integration message's id, carried from the outbox row. Uniquely indexed: this
    /// is the single point at which at-least-once delivery becomes exactly-once storage.
    /// </summary>
    public Guid MessageId { get; private set; }

    public Guid UserId { get; private set; }
    public Guid AlertRuleId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public AlertDirection Direction { get; private set; }
    public decimal Threshold { get; private set; }
    public decimal TriggeredPrice { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public bool IsRead { get; private set; }

    private Notification() { }

    public static Notification Create(
        Guid messageId,
        Guid userId,
        Guid alertRuleId,
        string ticker,
        AlertDirection direction,
        decimal threshold,
        decimal triggeredPrice,
        DateTimeOffset occurredUtc,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = messageId,
        UserId = userId,
        AlertRuleId = alertRuleId,
        Ticker = ticker,
        Direction = direction,
        Threshold = threshold,
        TriggeredPrice = triggeredPrice,
        OccurredUtc = occurredUtc,
        CreatedUtc = now
    };

    public void MarkRead() => IsRead = true;
}
