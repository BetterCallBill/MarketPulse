using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Authentication;

/// <summary>
/// The one place a session is minted. Registration, login and refresh all land here so
/// the token pair can never drift apart between entry points.
/// </summary>
internal static class SessionFactory
{
    public static async Task<AuthResult> IssueAsync(
        User user,
        IRefreshTokenRepository refreshTokens,
        ITokenService tokens,
        JwtOptions jwt,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (token, hash) = tokens.CreateRefreshToken();

        var refresh = RefreshToken.Issue(user.Id, hash, now, jwt.RefreshTokenLifetime);
        await refreshTokens.AddAsync(refresh, ct);
        await refreshTokens.SaveChangesAsync(ct);

        return new AuthResult(
            user.Id,
            user.Email,
            tokens.CreateAccessToken(user),
            now.Add(jwt.AccessTokenLifetime),
            token,
            refresh.ExpiresUtc);
    }
}
