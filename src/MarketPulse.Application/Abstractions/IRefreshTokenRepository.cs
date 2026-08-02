using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);
    Task AddAsync(RefreshToken token, CancellationToken ct);

    /// <summary>
    /// Revokes every still-active token for the user. This is the "family" in
    /// reuse detection: one replayed token kills every live session that user has.
    /// </summary>
    Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
