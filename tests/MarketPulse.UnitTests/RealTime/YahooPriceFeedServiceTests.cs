using System.Globalization;
using System.Net;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Persistence;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarketPulse.UnitTests.RealTime;

/// <summary>
/// Tests the real service end to end (real <see cref="YahooQuoteClient"/>, real
/// <see cref="PriceTickChannel"/>) but over a plain <see cref="HttpClient"/> wrapping
/// <see cref="ScriptedHandler"/> — no resilience pipeline. Task 3 already proved the
/// pipeline in isolation against a <see cref="FakeTimeProvider"/>; composing it here would
/// fight this file's fake clock (the pipeline has its own retry/breaker timing) for no
/// additional coverage, since the per-symbol catch below treats a pipeline exhaustion and a
/// plain-client failure identically. <c>MarketDataCompositionTests</c> covers only which
/// hosted service composition selects (<c>FakeTickService</c> vs. this one) — it does not
/// exercise the resilience pipeline being wired onto the typed client at all; that claim
/// belongs to <c>MarketDataResilienceTests</c> and this file alone.
/// </summary>
public class YahooPriceFeedServiceTests
{
    private static readonly string[] Codes = [.. SeedData.ReferenceTickers.Select(t => t.Code)];

    private static string ChartJson(decimal price) => $$"""
    {
      "chart": {
        "result": [ { "meta": { "regularMarketPrice": {{price.ToString(CultureInfo.InvariantCulture)}} } } ],
        "error": null
      }
    }
    """;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>Reads the ASX code a scripted request is asking about, from its Yahoo path segment.</summary>
    private static string CodeOf(HttpRequestMessage request) =>
        YahooSymbols.ToAsx(request.RequestUri!.Segments[^1])!;

    private static (YahooPriceFeedService Service, PriceTickChannel Channel, FakeTimeProvider Clock, TimeSpan Interval)
        Build(HttpMessageHandler handler)
    {
        var clock = new FakeTimeProvider();
        var options = new MarketDataOptions
        {
            PollInterval = TimeSpan.FromSeconds(20),
            AttemptTimeout = TimeSpan.FromSeconds(5)
        };
        var client = new YahooQuoteClient(new HttpClient(handler) { BaseAddress = new Uri("https://stub.local") });
        var channel = new PriceTickChannel();
        var service = new YahooPriceFeedService(
            client, channel, Options.Create(options), NullLogger<YahooPriceFeedService>.Instance, clock);

        return (service, channel, clock, options.PollInterval);
    }

    /// <summary>
    /// Nudges the fake clock forward in small steps until <paramref name="untilTrue"/>
    /// observes the resulting poll's effect (a channel count, a call counter) — not a fixed
    /// number of retries, and deliberately not a single jump straight to "one interval from
    /// now". <see cref="FakeTimeProvider.CreateTimer"/> schedules a timer's due time
    /// relative to the clock's value *at the moment it registers* — and because the
    /// service's startup `await Task.Yield()` means that registration may lag behind this
    /// method being called, jumping straight to a precomputed "one interval from now"
    /// target can lose the race: if the clock already reads that target by the time the
    /// PeriodicTimer registers, its due time becomes target-plus-another-interval, and every
    /// further call setting the clock to the same (now-stale) target is a no-op — the poll
    /// never fires. Stepping forward in increments small relative to the interval, and
    /// simply continuing past any one step that turns out to be premature, reaches whatever
    /// the actual due time turns out to be regardless of when registration happens, without
    /// ever waiting out the configured interval in real time.
    /// </summary>
    private static async Task AdvancePollUntilAsync(
        FakeTimeProvider clock, TimeSpan interval, Func<bool> untilTrue)
    {
        var step = TimeSpan.FromTicks(interval.Ticks / 50);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!untilTrue() && DateTime.UtcNow < deadline)
        {
            clock.Advance(step);
            await Task.Delay(10);
        }

        Assert.True(untilTrue(), "Timed out waiting for the scripted poll to take effect.");
    }

    private static async Task<PriceTick> ReadOneAsync(PriceTickChannel channel) =>
        await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>
    /// xUnit's async test methods run under <c>AsyncTestSyncContext</c>, so an in-test
    /// `await Task.Yield()` posts its continuation through that context rather than
    /// straight to the thread pool. BackgroundService.StartAsync calls ExecuteAsync
    /// synchronously up to its own `await Task.Yield()`, so without escaping that context
    /// first, the service's post-yield continuation (the PeriodicTimer registration
    /// included) can queue up behind the test's own continuations rather than run
    /// promptly. Starting from a raw <see cref="Task.Run(Action)"/> — which runs with no
    /// captured <see cref="SynchronizationContext"/> — keeps the service's continuations on
    /// the thread pool throughout, matching how it actually runs in the hosted process.
    /// </summary>
    private static Task StartAsync(YahooPriceFeedService service) =>
        Task.Run(() => service.StartAsync(CancellationToken.None));

    [Fact]
    public async Task A_poll_writes_one_tick_per_seeded_symbol()
    {
        var prices = Codes
            .Select((code, i) => (code, price: 10m + i))
            .ToDictionary(x => x.code, x => x.price);

        var handler = new ScriptedHandler(request => Json(ChartJson(prices[CodeOf(request)])));
        var (service, channel, clock, interval) = Build(handler);

        await StartAsync(service);
        try
        {
            await AdvancePollUntilAsync(clock, interval, () => channel.Reader.Count >= Codes.Length);

            var ticks = new List<PriceTick>();
            for (var i = 0; i < Codes.Length; i++)
            {
                ticks.Add(await ReadOneAsync(channel));
            }

            Assert.Equal(Codes.Length, ticks.Count);

            foreach (var code in Codes)
            {
                var tick = Assert.Single(ticks, t => t.Ticker == code);
                Assert.DoesNotContain('.', tick.Ticker);
                Assert.True(tick.Price > 0);
            }

            // Staggered starts (see PollOnceAsync) mean each symbol's tick is timestamped
            // at its own request's start, not one shared instant: timestamps taken in
            // Codes order are non-decreasing, and the whole poll's span never exceeds the
            // stagger window PollOnceAsync computes (half the poll interval).
            var orderedTimestamps = Codes
                .Select(code => ticks.Single(t => t.Ticker == code).TimestampUtc)
                .ToList();
            for (var i = 1; i < orderedTimestamps.Count; i++)
            {
                Assert.True(orderedTimestamps[i] >= orderedTimestamps[i - 1]);
            }
            Assert.True(orderedTimestamps[^1] - orderedTimestamps[0] <= interval / 2);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Proves PollOnceAsync's stagger, not just that all 25 requests eventually arrive:
    /// request *starts* are spread across the poll window rather than fired as one
    /// simultaneous burst (the shape that drew sustained 429s in this slice's manual
    /// rehearsal — see DependencyInjection's YahooUserAgent comment). ScriptedHandler
    /// records each request's arrival against the same FakeTimeProvider the service's
    /// Task.Delay(gap * index, timeProvider, ct) calls run on, so the recorded virtual
    /// timestamps are exactly the delay boundaries PollOnceAsync computes — not a proxy
    /// for them.
    /// </summary>
    [Fact]
    public async Task Poll_request_starts_are_staggered_across_the_poll_window()
    {
        var handler = new ScriptedHandler(request => Json(ChartJson(10m)));
        var (service, channel, clock, interval) = Build(handler);
        handler.Clock = clock;

        await StartAsync(service);
        try
        {
            await AdvancePollUntilAsync(clock, interval, () => channel.Reader.Count >= Codes.Length);

            Assert.Equal(Codes.Length, handler.RequestTimes.Count);

            // PollOnceAsync's own formula: half the poll interval spread across every
            // symbol, one gap per index. Recomputed here rather than imported so the test
            // pins the contract (options in, gap out), not the implementation detail of
            // how PollOnceAsync happens to compute it.
            var expectedGap = interval / 2 / Codes.Length;

            for (var i = 1; i < handler.RequestTimes.Count; i++)
            {
                Assert.Equal(expectedGap, handler.RequestTimes[i] - handler.RequestTimes[i - 1]);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_bad_quote_is_skipped_while_siblings_tick()
    {
        var badCode = Codes[0];
        var prices = Codes
            .Select((code, i) => (code, price: 20m + i))
            .ToDictionary(x => x.code, x => x.price);

        var handler = new ScriptedHandler(request =>
        {
            var code = CodeOf(request);
            return code == badCode
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(ChartJson(prices[code]));
        });

        var (service, channel, clock, interval) = Build(handler);

        await StartAsync(service);
        try
        {
            await AdvancePollUntilAsync(clock, interval, () => channel.Reader.Count >= Codes.Length - 1);

            var ticks = new List<PriceTick>();
            for (var i = 0; i < Codes.Length - 1; i++)
            {
                ticks.Add(await ReadOneAsync(channel));
            }

            Assert.Equal(Codes.Length - 1, ticks.Count);
            Assert.DoesNotContain(ticks, t => t.Ticker == badCode);

            // Count-only assertion for the warning: YahooQuoteClient turns the 500 into a
            // null price (no pipeline to exhaust), and the service logs a warning on that
            // branch before moving on — the channel count above is what actually pins
            // "siblings still tick", so a captured test logger would only re-assert the same
            // per-symbol catch this already exercises.
            Assert.False(channel.Reader.TryRead(out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_failed_poll_writes_nothing_and_the_next_one_self_heals()
    {
        var prices = Codes
            .Select((code, i) => (code, price: 30m + i))
            .ToDictionary(x => x.code, x => x.price);

        var seen = 0;
        var handler = new ScriptedHandler(request =>
        {
            var call = Interlocked.Increment(ref seen);
            // Every symbol is polled every tick, so the first Codes.Length requests are
            // poll 1 and the next Codes.Length are poll 2, regardless of interleaving —
            // ScriptedHandler has no real async suspension, so requests within one poll
            // complete before the test advances the clock again.
            return call <= Codes.Length
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(ChartJson(prices[CodeOf(request)]));
        });

        var (service, channel, clock, interval) = Build(handler);

        await StartAsync(service);
        try
        {
            // The channel stays empty on a failed poll, so the observable effect to wait
            // for is the request count, not a channel write.
            await AdvancePollUntilAsync(clock, interval, () => Volatile.Read(ref seen) >= Codes.Length);
            Assert.False(channel.Reader.TryRead(out _));

            await AdvancePollUntilAsync(clock, interval, () => channel.Reader.Count >= Codes.Length);

            var ticks = new List<PriceTick>();
            for (var i = 0; i < Codes.Length; i++)
            {
                ticks.Add(await ReadOneAsync(channel));
            }

            Assert.Equal(Codes.Length, ticks.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stopping_the_service_completes_promptly()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var (service, _, _, _) = Build(handler);

        await StartAsync(service);

        // Same shutdown-contract idiom as BrokerOutageTests: the stop task itself must win
        // the race against a generous bound, not merely complete eventually.
        var stop = service.StopAsync(CancellationToken.None);
        Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10))));
    }
}
