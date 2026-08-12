using System.Diagnostics;
using System.Text;
using MarketPulse.Application.Telemetry;
using MarketPulse.Infrastructure.Messaging;
using NSubstitute;
using RabbitMQ.Client;

namespace MarketPulse.UnitTests.Messaging;

public class MessagingTelemetryTests : IDisposable
{
    private readonly ActivityListener _listener;

    public MessagingTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == global::MarketPulse.Application.Telemetry.Telemetry.MessagingSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void Producer_activity_injects_traceparent_into_headers()
    {
        var properties = new BasicProperties();

        using var activity = MessagingTelemetry.StartProducerActivity("ex", properties);

        Assert.NotNull(activity);
        Assert.NotNull(properties.Headers);
        var raw = Assert.IsType<byte[]>(properties.Headers!["traceparent"]);
        Assert.Contains(activity!.TraceId.ToString(), Encoding.UTF8.GetString(raw));
    }

    [Fact]
    public void Consumer_activity_restores_the_producer_trace()
    {
        var properties = new BasicProperties();
        using var producer = MessagingTelemetry.StartProducerActivity("ex", properties);

        var incoming = Substitute.For<IReadOnlyBasicProperties>();
        incoming.Headers.Returns(properties.Headers);

        using var consumer = MessagingTelemetry.StartConsumerActivity("q", incoming);

        Assert.NotNull(consumer);
        Assert.Equal(producer!.TraceId, consumer!.TraceId);
    }

    [Fact]
    public void Missing_headers_still_produce_a_consumer_activity()
    {
        var incoming = Substitute.For<IReadOnlyBasicProperties>();
        incoming.Headers.Returns((IDictionary<string, object?>?)null);

        using var consumer = MessagingTelemetry.StartConsumerActivity("q", incoming);

        Assert.NotNull(consumer); // a root span, no remote parent
    }
}
