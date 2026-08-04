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

    /// <summary>
    /// Takes an enqueued event back off the current unit of work, for the case where the
    /// state change it was going to announce did not commit after all.
    ///
    /// <para>The counterpart to <see cref="EnqueueAsync"/> not saving: a failed save leaves
    /// everything it tried to write still pending on the unit of work, so an event enqueued
    /// beside a state change that lost an optimistic-concurrency race would otherwise be
    /// written by the <em>next</em> save on that same unit of work — announcing something
    /// that never happened.</para>
    /// </summary>
    void Discard(Guid messageId);
}
