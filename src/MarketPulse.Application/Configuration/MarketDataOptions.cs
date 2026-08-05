using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

/// <summary>
/// Which tick producer runs, and how the real one behaves. Lives in Application beside
/// <see cref="RabbitMqOptions"/> for the same reason: bound and validated at startup
/// without the host referencing the implementation.
/// </summary>
public sealed class MarketDataOptions : IValidatableObject
{
    public const string SectionName = "MarketData";

    public const string FakeSource = "Fake";
    public const string YahooSource = "Yahoo";

    /// <summary>"Fake" (default) or "Yahoo". Fake stays the default so tests and offline
    /// development never depend on a third party.</summary>
    [Required]
    public string Source { get; init; } = FakeSource;

    /// <summary>Configuration so tests point the client at a stubbed handler's base and
    /// ADR-010 names the real value in one place — not a multi-provider abstraction.</summary>
    [Required]
    public string BaseUrl { get; init; } = "https://query1.finance.yahoo.com";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Per-attempt cap, well under the poll interval so a hung upstream can
    /// never stack polls.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.Equals(Source, FakeSource, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Source, YahooSource, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                $"MarketData:Source must be '{FakeSource}' or '{YahooSource}'.",
                [nameof(Source)]);
        }

        if (PollInterval < TimeSpan.FromSeconds(1))
        {
            yield return new ValidationResult(
                "MarketData:PollInterval must be at least one second.", [nameof(PollInterval)]);
        }

        if (AttemptTimeout <= TimeSpan.Zero || AttemptTimeout >= PollInterval)
        {
            yield return new ValidationResult(
                "MarketData:AttemptTimeout must be positive and shorter than the poll interval.",
                [nameof(AttemptTimeout)]);
        }
    }
}
