using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface ITokenService
{
    /// <summary>Signed JWT carrying the user id as `sub`. Lifetime comes from JwtOptions.</summary>
    string CreateAccessToken(User user);

    /// <summary>
    /// A new 256-bit random token and its SHA-256 hash. The caller stores the hash and
    /// returns the token to the browser.
    /// </summary>
    (string Token, string Hash) CreateRefreshToken();

    /// <summary>Hashes a token presented by a client, so it can be looked up.</summary>
    string HashRefreshToken(string token);
}
