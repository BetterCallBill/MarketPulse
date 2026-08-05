using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Backs the stored-key idempotency filter. Deliberately not built on the request's own
/// unit of work — see <c>IdempotencyStore</c>'s doc comment for why each method here saves
/// immediately rather than deferring to the action's SaveChanges.
/// </summary>
public interface IIdempotencyStore
{
    Task<IdempotencyKey?> FindAsync(Guid userId, string endpoint, string key, CancellationToken ct);

    /// <summary>Returns false when a concurrent claim already won the unique index.</summary>
    Task<bool> TryClaimAsync(IdempotencyKey claim, CancellationToken ct);

    Task CompleteAsync(IdempotencyKey claim, int statusCode, string body, CancellationToken ct);

    Task RemoveAsync(IdempotencyKey claim, CancellationToken ct);
}
