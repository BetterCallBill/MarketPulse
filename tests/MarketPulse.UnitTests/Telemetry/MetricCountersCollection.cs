namespace MarketPulse.UnitTests.Telemetry;

/// <summary>
/// Groups every unit test class that measures an exact count or delta on one of
/// <c>Telemetry</c>'s process-global <c>Counter&lt;long&gt;</c> instruments (static, one per
/// process, by design — see Telemetry.cs). xUnit parallelises across test classes by
/// default; two classes each attaching a fresh <c>MeterListener</c> to the same static
/// counter and racing to <c>Add</c>/assert on it produces a latent off-by-one flake, because
/// every started listener observes every Add on that instrument, not just the ones its own
/// test triggered. Collection membership makes xUnit run these classes sequentially relative
/// to each other — nothing else in the suite is affected.
/// </summary>
[CollectionDefinition("MetricCounters")]
public sealed class MetricCountersCollection;
