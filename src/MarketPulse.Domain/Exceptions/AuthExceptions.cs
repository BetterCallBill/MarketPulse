namespace MarketPulse.Domain.Exceptions;

/// <summary>
/// Deliberately identical whether the email is unknown or the password is wrong —
/// distinguishing them hands an attacker an account-enumeration oracle.
/// </summary>
public sealed class InvalidCredentialsException()
    : DomainException("Email or password is incorrect.")
{
    public override string ErrorCode => "invalid-credentials";
    public override int StatusCode => 401;
}

public sealed class AccountLockedException(TimeSpan retryAfter)
    : DomainException("Too many failed attempts. Try again later.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
    public override string ErrorCode => "account-locked";
    public override int StatusCode => 429;
}

public sealed class EmailTakenException()
    : DomainException("That email address is already registered.")
{
    public override string ErrorCode => "email-taken";
}

/// <summary>
/// Raised when an already-revoked refresh token is presented, which means it leaked.
/// The handler revokes the user's whole token family in response.
/// </summary>
public sealed class SessionRevokedException()
    : DomainException("This session is no longer valid. Sign in again.")
{
    public override string ErrorCode => "session-revoked";
    public override int StatusCode => 401;
}

public sealed class CsrfValidationException()
    : DomainException("Missing or invalid CSRF token.")
{
    public override string ErrorCode => "csrf-failed";
    public override int StatusCode => 403;
}
