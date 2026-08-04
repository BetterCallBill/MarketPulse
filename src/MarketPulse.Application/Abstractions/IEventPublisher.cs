namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Publishes one integration event and returns only once the broker has confirmed it.
/// A faulted task means "not confirmed" and must leave the outbox row pending.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(
        string routingKey,
        Guid messageId,
        string payload,
        string? correlationId,
        CancellationToken ct);
}
