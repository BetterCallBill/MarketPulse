namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Resolves the caller's identity. Backed by DevAuthMiddleware in slice 1 and
/// by JWT claims from slice 2 onward — no consumer changes when that swap happens.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }
}
