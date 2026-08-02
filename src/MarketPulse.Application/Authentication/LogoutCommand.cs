using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Authentication;

public record LogoutCommand(string? RefreshToken) : IRequest<Unit>;

public sealed class LogoutHandler(
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens)
    : IRequestHandler<LogoutCommand, Unit>
{
    /// <summary>
    /// Deliberately forgiving: logging out with a missing or already-dead token is a
    /// success, not an error. The API layer clears the cookies either way.
    /// </summary>
    public async Task<Unit> Handle(LogoutCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unit.Value;
        }

        var existing = await refreshTokens.GetByHashAsync(
            tokens.HashRefreshToken(request.RefreshToken), ct);

        if (existing is not null)
        {
            await refreshTokens.RevokeAllForUserAsync(existing.UserId, DateTimeOffset.UtcNow, ct);
            await refreshTokens.SaveChangesAsync(ct);
        }

        return Unit.Value;
    }
}
