namespace MarketPulse.Domain.Entities;

public sealed class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockoutEndUtc { get; private set; }

    private User() { }

    /// <summary>
    /// Used by the seed data, which needs a fixed id, and by tests. Application code
    /// registers users through <see cref="Register"/>.
    /// </summary>
    public User(Guid id, string email, string passwordHash, DateTimeOffset createdUtc)
    {
        Id = id;
        Email = Normalise(email);
        PasswordHash = passwordHash;
        CreatedUtc = createdUtc;
    }

    public static User Register(string email, string passwordHash) => new(
        Guid.NewGuid(), email, passwordHash, DateTimeOffset.UtcNow);

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndUtc is { } end && end > now;

    public void RecordFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;

        if (FailedLoginCount >= maxAttempts)
        {
            LockoutEndUtc = now + lockoutDuration;
        }
    }

    public void RecordSuccessfulLogin()
    {
        FailedLoginCount = 0;
        LockoutEndUtc = null;
    }

    /// <summary>Emails are compared and stored case-insensitively.</summary>
    public static string Normalise(string email) => email.Trim().ToLowerInvariant();
}
