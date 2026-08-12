using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

/// <summary>
/// Writes through the same scoped <see cref="MarketPulseDbContext"/> the repositories use,
/// so one SaveChangesAsync commits the rule's state change and this row in one transaction.
/// </summary>
public sealed class Outbox(MarketPulseDbContext db) : IOutbox
{
    public async Task EnqueueAsync(
        Guid messageId,
        string type,
        string payload,
        string? correlationId,
        DateTimeOffset occurredUtc,
        CancellationToken ct) =>
        await db.OutboxMessages.AddAsync(
            OutboxMessage.Create(messageId, type, payload, correlationId, occurredUtc), ct);

    public void Discard(Guid messageId)
    {
        var entry = db.ChangeTracker
            .Entries<OutboxMessage>()
            .FirstOrDefault(e => e.State == EntityState.Added && e.Entity.Id == messageId);

        // Detached rather than removed: the row was never inserted, so there is nothing for a
        // DELETE to target. Detaching is what stops the pending INSERT from being replayed by
        // the next SaveChangesAsync on this context.
        if (entry is not null)
        {
            entry.State = EntityState.Detached;
        }
    }
}
