using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;

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
}
