using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.Authentication;

public record RefreshSessionCommand(string RefreshToken) : IRequest<AuthResult>;

public sealed class RefreshSessionHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    IOptions<JwtOptions> jwt)
    : IRequestHandler<RefreshSessionCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RefreshSessionCommand request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = tokens.HashRefreshToken(request.RefreshToken);
        var existing = await refreshTokens.GetByHashAsync(hash, ct);

        if (existing is null)
        {
            throw new SessionRevokedException();
        }

        // A token we issued, already revoked, presented again: it leaked. Whoever holds
        // it and whoever holds its successor are indistinguishable from here, so end
        // every live session this user has and make them sign in again.
        if (existing.RevokedUtc is not null)
        {
            await refreshTokens.RevokeAllForUserAsync(existing.UserId, now, ct);
            await refreshTokens.SaveChangesAsync(ct);
            throw new SessionRevokedException();
        }

        if (!existing.IsActive(now))
        {
            throw new SessionRevokedException();
        }

        var user = await users.GetByIdAsync(existing.UserId, ct)
            ?? throw new SessionRevokedException();

        var (token, newHash) = tokens.CreateRefreshToken();
        var replacement = RefreshToken.Issue(user.Id, newHash, now, jwt.Value.RefreshTokenLifetime);

        existing.ReplaceWith(replacement.Id, now);
        await refreshTokens.AddAsync(replacement, ct);
        await refreshTokens.SaveChangesAsync(ct);

        return new AuthResult(
            user.Id,
            user.Email,
            tokens.CreateAccessToken(user),
            now.Add(jwt.Value.AccessTokenLifetime),
            token,
            replacement.ExpiresUtc);
    }
}
