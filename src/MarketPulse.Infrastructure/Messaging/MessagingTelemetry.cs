using System.Diagnostics;
using System.Text;
using MarketPulse.Application.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// W3C trace context across the broker, by hand — RabbitMQ.Client has no built-in
/// propagation. The CorrelationId basic property stays alongside: that is the
/// human-facing key, this is the machine-facing one; they are complementary.
/// </summary>
public static class MessagingTelemetry
{
    // Not Propagators.DefaultTextMapPropagator: that static defaults to a no-op unless
    // something calls Sdk.SetDefaultTextMapPropagator during host startup, which neither
    // host does. Building the composite directly here means broker propagation works
    // regardless of that ambient, mutable, process-wide setting.
    private static readonly TextMapPropagator Propagator = new CompositeTextMapPropagator(
    [
        new TraceContextPropagator(),
        new BaggagePropagator()
    ]);

    public static Activity? StartProducerActivity(string exchange, BasicProperties properties)
    {
        var activity = Telemetry.MessagingSource.StartActivity(
            $"{exchange} publish", ActivityKind.Producer);

        var contextToInject = activity?.Context
            ?? Activity.Current?.Context
            ?? default;

        properties.Headers ??= new Dictionary<string, object?>();
        Propagator.Inject(
            new PropagationContext(contextToInject, Baggage.Current),
            properties.Headers,
            static (headers, key, value) => headers[key] = Encoding.UTF8.GetBytes(value));

        return activity;
    }

    public static Activity? StartConsumerActivity(string queue, IReadOnlyBasicProperties properties)
    {
        var parent = Propagator.Extract(default, properties.Headers, static (headers, key) =>
        {
            if (headers is not null && headers.TryGetValue(key, out var value))
            {
                return value switch
                {
                    byte[] bytes => new[] { Encoding.UTF8.GetString(bytes) },
                    string s => new[] { s },
                    _ => Array.Empty<string>(),
                };
            }

            return Array.Empty<string>();
        });

        return Telemetry.MessagingSource.StartActivity(
            $"{queue} consume", ActivityKind.Consumer, parent.ActivityContext);
    }
}
