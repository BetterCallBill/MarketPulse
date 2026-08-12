using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

/// <summary>
/// Lives in Application, not Infrastructure, so both hosts can bind and validate it at
/// startup without either taking a dependency on the broker client. The names are
/// configuration rather than constants because the integration tests and a future
/// multi-environment deployment both need to vary them.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string HostName { get; init; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; init; } = 5672;

    [Required]
    public string UserName { get; init; } = "marketpulse";

    [Required]
    public string Password { get; init; } = "Local!Dev!Pass123";

    [Required]
    public string VirtualHost { get; init; } = "/";

    public string PricesExchange { get; init; } = "marketpulse.prices";
    public string AlertsExchange { get; init; } = "marketpulse.alerts";
    public string AlertsDeadLetterExchange { get; init; } = "marketpulse.alerts.dlx";

    public string PricesQueue { get; init; } = "alerts.prices";
    public string NotificationsQueue { get; init; } = "api.notifications";
    public string NotificationsDeadLetterQueue { get; init; } = "api.notifications.dlq";

    /// <summary>
    /// A worker that was down for a minute must evaluate current prices, not a backlog of
    /// stale ones. Five seconds is comfortably longer than the one-second tick interval.
    /// </summary>
    [Range(100, 600_000)]
    public int TickTtlMilliseconds { get; init; } = 5_000;

    [Range(1, 1_000_000)]
    public int TickQueueMaxLength { get; init; } = 1_000;

    [Range(1, 65535)]
    public ushort PrefetchCount { get; init; } = 100;

    /// <summary>
    /// Bound on the transient-failure redelivery loop (ADR-009): a message that fails
    /// transiently this many times is dead-lettered instead of retried forever.
    /// </summary>
    [Range(1, 100)]
    public int RetryLimit { get; init; } = 5;

    /// <summary>Cap on the connection retry backoff.</summary>
    public TimeSpan MaxConnectionRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
