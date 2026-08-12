namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Resolves the caller's identity, read from the `sub` claim of the JWT the bearer
/// handler validates. Slice 1 backed this with a development stub instead; no consumer
/// changed when that was swapped for real authentication, which was the point of the seam.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }
}
