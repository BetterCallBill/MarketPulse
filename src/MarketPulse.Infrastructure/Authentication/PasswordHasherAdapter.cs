using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace MarketPulse.Infrastructure.Authentication;

/// <summary>
/// Wraps ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/> — PBKDF2-HMAC-SHA512,
/// 100,000 iterations, 128-bit random salt per hash. We reuse the audited implementation
/// but not the rest of Identity: <c>PasswordHasher&lt;T&gt;</c> is constrained to
/// <c>class</c>, not to <c>IdentityUser</c>, so no Identity type crosses into the domain.
/// </summary>
public sealed class PasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();

    /// <summary>
    /// Any non-null user instance works — <see cref="PasswordHasher{TUser}"/> ignores the
    /// argument entirely and salts randomly instead.
    /// </summary>
    private static readonly User Unused =
        new(Guid.Empty, "unused@marketpulse.local", string.Empty, DateTimeOffset.UnixEpoch);

    public string Hash(string password) => _inner.HashPassword(Unused, password);

    public bool Verify(string hash, string password)
    {
        try
        {
            var result = _inner.VerifyHashedPassword(Unused, hash, password);
            return result is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            // A hash that is not valid base64 — corrupt or hand-edited data.
            return false;
        }
    }
}
