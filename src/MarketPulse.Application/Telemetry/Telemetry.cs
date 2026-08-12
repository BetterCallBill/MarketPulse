using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MarketPulse.Application.Telemetry;

/// <summary>
/// The one place instruments are created. Lives in Application (BCL diagnostics only) so
/// handlers and Infrastructure can both record without a layering violation; the hosts
/// subscribe by name via AddMeter/AddSource. Static because instruments are process-wide
/// by design — a Meter is not per-request state.
/// </summary>
public static class Telemetry
{
    public const string MeterName = "MarketPulse";
    public const string MessagingSourceName = "MarketPulse.Messaging";

    public static readonly Meter Meter = new(MeterName);
    public static readonly ActivitySource MessagingSource = new(MessagingSourceName);

    public static readonly Counter<long> TicksPersisted =
        Meter.CreateCounter<long>("marketpulse.ticks.persisted");
    public static readonly Counter<long> TickBufferDrops =
        Meter.CreateCounter<long>("marketpulse.ticks.buffer_drops");
    public static readonly Counter<long> AlertsEvaluated =
        Meter.CreateCounter<long>("marketpulse.alerts.evaluated");
    public static readonly Counter<long> AlertsTriggered =
        Meter.CreateCounter<long>("marketpulse.alerts.triggered");
    public static readonly Counter<long> NotificationRedeliveries =
        Meter.CreateCounter<long>("marketpulse.notifications.redeliveries");
    public static readonly Counter<long> NotificationDeadLetters =
        Meter.CreateCounter<long>("marketpulse.notifications.dead_letters");
    public static readonly Counter<long> AuthLoginFailures =
        Meter.CreateCounter<long>("marketpulse.auth.login_failures");
    public static readonly Counter<long> AuthLockouts =
        Meter.CreateCounter<long>("marketpulse.auth.lockouts");
    public static readonly Counter<long> AuthRefreshReuse =
        Meter.CreateCounter<long>("marketpulse.auth.refresh_reuse_detected");
}
