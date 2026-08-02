using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.Authentication;

public record LoginCommand(string Email, string Password) : IRequest<AuthResult>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        // Shape only. The password policy is deliberately NOT applied here — an existing
        // account whose password predates a policy change must still be able to sign in.
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.").WithErrorCode("invalid-email");
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.").WithErrorCode("invalid-password");
    }
}

public sealed class LoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    IOptions<JwtOptions> jwt,
    IOptions<AuthOptions> auth)
    : IRequestHandler<LoginCommand, AuthResult>
{
    /// <summary>
    /// A real PBKDF2 hash of a value nobody knows. Verifying against it when the email is
    /// unknown keeps the response time of "no such user" indistinguishable from
    /// "wrong password". It must be a genuinely well-formed hash — a made-up string would
    /// fail format parsing and return in microseconds, which is the exact timing signal
    /// this is meant to remove.
    /// </summary>
    private const string DummyHash =
        "AQAAAAIAAYagAAAAEPZcHXnPXPdj4H3vE6fFl5DafQTg4JAma85FIiOEhrmYiGGS7m+S2s8ECgKI+wxdqg==";

    public async Task<AuthResult> Handle(LoginCommand request, CancellationToken ct)
    {
        var options = auth.Value;
        var now = DateTimeOffset.UtcNow;
        var user = await users.GetByEmailAsync(request.Email, ct);

        if (user is null)
        {
            hasher.Verify(DummyHash, request.Password);
            throw new InvalidCredentialsException();
        }

        if (user.IsLockedOut(now))
        {
            throw new AccountLockedException(user.LockoutEndUtc!.Value - now);
        }

        if (!hasher.Verify(user.PasswordHash, request.Password))
        {
            user.RecordFailedLogin(now, options.MaxFailedAttempts, options.LockoutDuration);
            await users.SaveChangesAsync(ct);
            throw new InvalidCredentialsException();
        }

        user.RecordSuccessfulLogin();
        await users.SaveChangesAsync(ct);

        return await SessionFactory.IssueAsync(user, refreshTokens, tokens, jwt.Value, ct);
    }
}
