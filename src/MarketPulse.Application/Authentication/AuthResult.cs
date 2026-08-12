namespace MarketPulse.Application.Authentication;

/// <summary>
/// What the authentication handlers hand back. Deliberately transport-agnostic — the
/// API layer decides these become cookies, and mints the CSRF nonce separately.
/// </summary>
public sealed record AuthResult(
    Guid UserId,
    string Email,
    string AccessToken,
    DateTimeOffset AccessExpiresUtc,
    string RefreshToken,
    DateTimeOffset RefreshExpiresUtc);
