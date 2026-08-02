namespace MarketPulse.Domain.Entities;

/// <summary>
/// One issued refresh token. Only the SHA-256 hash of the token is ever stored — the
/// token itself exists in the response cookie and nowhere else, so a database dump
/// yields no usable sessions.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset ExpiresUtc { get; private set; }
    public DateTimeOffset? RevokedUtc { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(
        Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = tokenHash,
        CreatedUtc = now,
        ExpiresUtc = now + lifetime
    };

    public bool IsActive(DateTimeOffset now) => RevokedUtc is null && ExpiresUtc > now;

    public void Revoke(DateTimeOffset now) => RevokedUtc ??= now;

    /// <summary>
    /// Rotation: revoke this token and record which token superseded it. The successor
    /// id is what makes a replayed token traceable to a live family.
    /// </summary>
    public void ReplaceWith(Guid replacementId, DateTimeOffset now)
    {
        Revoke(now);
        ReplacedByTokenId = replacementId;
    }
}
