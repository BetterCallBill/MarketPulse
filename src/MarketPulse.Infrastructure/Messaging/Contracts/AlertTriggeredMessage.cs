namespace MarketPulse.Infrastructure.Messaging.Contracts;

/// <summary>
/// Published by the worker's outbox dispatcher, consumed by the API. <see cref="MessageId"/>
/// is the outbox row's id and the consumer's dedupe key — the one field that makes
/// at-least-once delivery safe.
/// </summary>
public sealed record AlertTriggeredMessage(
    Guid MessageId,
    Guid AlertRuleId,
    Guid UserId,
    string Ticker,
    string Direction,
    decimal Threshold,
    decimal TriggeredPrice,
    DateTimeOffset OccurredUtc);
