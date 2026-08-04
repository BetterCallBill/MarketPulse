namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Enqueues an integration event onto the *current* unit of work. It deliberately does not
/// save: the caller's single <c>SaveChangesAsync</c> is what makes the state change and the
/// event atomic, which is the entire point of the pattern. Primitives only, so Application
/// stays ignorant of both the broker and the wire contracts.
/// </summary>
public interface IOutbox
{
    Task EnqueueAsync(
        Guid messageId,
        string type,
        string payload,
        string? correlationId,
        DateTimeOffset occurredUtc,
        CancellationToken ct);
}
