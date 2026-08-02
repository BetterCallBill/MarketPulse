using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.Authentication;

public record RegisterUserCommand(string Email, string Password) : IRequest<AuthResult>;

public sealed class RegisterUserValidator : AbstractValidator<RegisterUserCommand>
{
    public RegisterUserValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.").WithErrorCode("invalid-email")
            .MaximumLength(256).WithMessage("Email must be 256 characters or fewer.").WithErrorCode("invalid-email")
            .EmailAddress().WithMessage("Enter a valid email address.").WithErrorCode("invalid-email");

        RuleFor(x => x.Password)
            .Must(PasswordPolicy.IsAcceptable)
            .WithMessage($"Password must be at least {PasswordPolicy.MinimumLength} characters " +
                         "and must not be a commonly used password.")
            .WithErrorCode("weak-password");
    }
}

public sealed class RegisterUserHandler(
    IUserRepository users,
    IWatchlistRepository watchlists,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    IOptions<JwtOptions> jwt)
    : IRequestHandler<RegisterUserCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RegisterUserCommand request, CancellationToken ct)
    {
        if (await users.EmailExistsAsync(request.Email, ct))
        {
            throw new EmailTakenException();
        }

        var user = User.Register(request.Email, hasher.Hash(request.Password));
        await users.AddAsync(user, ct);

        // Give the new account a watchlist up front so the dashboard has something to
        // read on first load.
        await watchlists.AddAsync(Watchlist.Create(user.Id), ct);
        await users.SaveChangesAsync(ct);

        return await SessionFactory.IssueAsync(user, refreshTokens, tokens, jwt.Value, ct);
    }
}
