using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(1, 100)]
    public int MaxFailedAttempts { get; init; } = 5;

    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-IP fixed window on login and register.</summary>
    [Range(1, 10_000)]
    public int LoginRequestsPerMinute { get; init; } = 10;
}
