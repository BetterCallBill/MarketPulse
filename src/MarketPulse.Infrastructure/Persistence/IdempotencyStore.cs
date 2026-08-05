using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

/// <summary>
/// The one deliberate second `DbContext` in this codebase. Claim and completion are their
/// own tiny units of work — not part of the action's — so this store must not share the
/// request's scoped `MarketPulseDbContext`: a claim added on that context would sit pending
/// until the action's own SaveChanges, which destroys claim-first semantics (the row has to
/// exist, committed, before the action runs, so a racing second request's insert can violate
/// the unique index against it). <see cref="IDbContextFactory{TContext}"/> gives each call
/// its own short-lived context, created and disposed per method.
/// </summary>
public sealed class IdempotencyStore(IDbContextFactory<MarketPulseDbContext> contextFactory)
    : IIdempotencyStore
{
    public async Task<IdempotencyKey?> FindAsync(
        Guid userId, string endpoint, string key, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.IdempotencyKeys.FirstOrDefaultAsync(
            k => k.UserId == userId && k.Endpoint == endpoint && k.Key == key, ct);
    }

    public async Task<bool> TryClaimAsync(IdempotencyKey claim, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.IdempotencyKeys.Add(claim);

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(claim).State = EntityState.Detached;
            return false;
        }
    }

    public async Task CompleteAsync(IdempotencyKey claim, int statusCode, string body, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.IdempotencyKeys.Attach(claim);
        claim.Complete(statusCode, body);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(IdempotencyKey claim, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.IdempotencyKeys.Attach(claim);
        db.IdempotencyKeys.Remove(claim);
        await db.SaveChangesAsync(ct);
    }

    // SQL Server unique-index/constraint violations: 2601 (duplicate key row), 2627
    // (violation of a unique constraint/PK).
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
