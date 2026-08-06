using System.Diagnostics.Metrics;
using MarketPulse.Application.Telemetry;

namespace MarketPulse.UnitTests.Telemetry;

public class TelemetryTests
{
    [Fact]
    public void Counters_emit_on_the_marketpulse_meter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == global::MarketPulse.Application.Telemetry.Telemetry.MeterName
                && instrument.Name == "marketpulse.auth.login_failures")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed += value);
        listener.Start();

        global::MarketPulse.Application.Telemetry.Telemetry.AuthLoginFailures.Add(1);

        listener.RecordObservableInstruments();
        Assert.Equal(1, observed);
    }
}
