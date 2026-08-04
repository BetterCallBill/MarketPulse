namespace MarketPulse.Domain.Entities;

/// <summary>
/// One integration event, written in the same transaction as the state change that caused
/// it. The dispatcher relays it and marks it dispatched only once the broker confirms.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>Also the message id on the wire, and the consumer's dedupe key.</summary>
    public Guid Id { get; private set; }

    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string? CorrelationId { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public DateTimeOffset? DispatchedUtc { get; private set; }
    public int AttemptCount { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create(
        Guid messageId,
        string type,
        string payload,
        string? correlationId,
        DateTimeOffset occurredUtc) => new()
    {
        Id = messageId,
        Type = type,
        Payload = payload,
        CorrelationId = correlationId,
        OccurredUtc = occurredUtc
    };

    public void MarkDispatched(DateTimeOffset now) => DispatchedUtc ??= now;

    public void RecordAttempt() => AttemptCount++;
}
