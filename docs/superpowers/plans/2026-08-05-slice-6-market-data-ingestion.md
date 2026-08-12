# Slice 6 — Real Market-Data Ingestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `YahooPriceFeedService` polling Yahoo's keyless `v8/finance/chart` endpoint per symbol behind a declared resilience pipeline (retry-with-jitter, circuit breaker, per-attempt timeout), writing into the existing `PriceTickChannel`; source selected by `MarketData:Source` with `Fake` the default; ADR-010 and docs reconciliation. Zero changes to any consumer.

**Architecture:** A second tick producer behind the slice-1 channel seam. Typed `HttpClient` (`YahooQuoteClient`) carries the resilience pipeline; the hosted service owns cadence, symbol fan-out, validation, and lossy-by-design semantics. Composition picks exactly one of `FakeTickService` / `YahooPriceFeedService` at registration time.

**Tech Stack:** .NET 10, `Microsoft.Extensions.Http.Resilience` (Polly v8 under the hood), `System.Text.Json`, xUnit + NSubstitute + `Microsoft.Extensions.TimeProvider.Testing` (breaker clock control), Testcontainers for the composition tests.

## Global Constraints

- Branch: `feature/slice-6-market-data-ingestion` (exists; spec + amendment committed). Merges into `test`.
- Conventional commits, **no Co-Authored-By trailer**.
- Spec: `docs/superpowers/specs/2026-08-05-market-data-ingestion-design.md` (note its poller amendment: per-symbol `v8/finance/chart`, 20s default). Out of scope there is out of scope here: no tick persistence, no rule cache, no dashboard changes, no provider abstraction, no live Yahoo calls in any automated suite.
- TDD with RED/GREEN evidence wherever a failing test is meaningful.
- `DependencyRuleTests` stays green; nothing new in Domain/Application.
- Central package management: new packages get a `PackageVersion` in `Directory.Packages.props` and a versionless `PackageReference` in the consuming csproj. Use the latest stable versions compatible with net10.0 (verify on nuget.org at implementation time; `Microsoft.Extensions.Http.Resilience` and `Microsoft.Extensions.TimeProvider.Testing` both track the extensions release train).
- Existing suites must pass **with zero changes to their code** — that is the spec's headline claim. If a change to an existing test seems needed, stop and report; it means the seam leaked.
- Run: `dotnet test tests/MarketPulse.UnitTests --filter <name>`; integration needs Docker (compose infra up).

---

### Task 1: Options, package, and composition skeleton

**Files:**
- Modify: `Directory.Packages.props` (+ `Microsoft.Extensions.Http.Resilience`)
- Modify: `src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj` (reference it)
- Create: `src/MarketPulse.Application/Configuration/MarketDataOptions.cs`
- Modify: `src/MarketPulse.Api/Program.cs` (bind + validate options; pass configuration into `AddInfrastructure`)
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (`AddInfrastructure` gains the source switch — still registering only the fake for `Fake`/default in this task; Yahoo lands in Task 4)
- Modify: `src/MarketPulse.Api/appsettings.json` (document the section with defaults)

**Interfaces:**
- Produces (later tasks bind to these exact members):
  - `MarketDataOptions` — `const string SectionName = "MarketData"`; `string Source` (default `"Fake"`); `string BaseUrl` (default `"https://query1.finance.yahoo.com"`); `TimeSpan PollInterval` (default 20s); `TimeSpan AttemptTimeout` (default 5s); validation: `Source` must be `"Fake"` or `"Yahoo"` (case-insensitive), `PollInterval` ≥ 1s, `AttemptTimeout` positive and less than `PollInterval`.
  - `AddInfrastructure(this IServiceCollection, string connectionString, IConfiguration configuration)` — reads `MarketData:Source` to host exactly one tick producer.

- [ ] **Step 1: Write `MarketDataOptions`**

Follow `RabbitMqOptions`'s shape (Application/Configuration, doc comment explaining why it lives there, DataAnnotations where they fit, an `IValidateOptions`-style check where they don't):

```csharp
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
```

- [ ] **Step 2: Bind in `Program.cs`** beside the other options blocks:

```csharp
builder.Services.AddOptions<MarketDataOptions>()
    .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

and change the call at the bottom to `builder.Services.AddInfrastructure(connectionString, builder.Configuration);`.

- [ ] **Step 3: The switch in `AddInfrastructure`**

```csharp
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration)
    {
        services.AddPersistence(connectionString);
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<PriceTickChannel>();

        // Exactly one tick producer. Read raw here because hosted-service registration
        // happens before options validation runs; a bad value still fails startup via
        // ValidateOnStart, and the composition tests pin both sides.
        var source = configuration[$"{MarketDataOptions.SectionName}:Source"];

        if (string.Equals(source, MarketDataOptions.YahooSource, StringComparison.OrdinalIgnoreCase))
        {
            // Task 4 replaces this line with the real registration.
            throw new NotSupportedException("MarketData:Source=Yahoo lands in a later task.");
        }

        services.AddHostedService<FakeTickService>();
        return services;
    }
```

Add the `MarketData` section to `appsettings.json` with the defaults and a one-line comment-style hint is not possible in JSON — instead document the allowed values in ADR-010 (Task 5) and keep the section minimal:

```json
  "MarketData": {
    "Source": "Fake"
  }
```

Add the `Microsoft.Extensions.Http.Resilience` package entries (props + csproj) now so Task 3 builds on a resolved package.

- [ ] **Step 4: Verify**

Run: `dotnet build MarketPulse.sln` (clean) and `dotnet test tests/MarketPulse.UnitTests` (green, incl. `DependencyRuleTests` — `MarketDataOptions` sits in Application with zero new references). Run one existing integration class to prove the `AddInfrastructure` signature change didn't break `TestFactory` composition: `dotnet test tests/MarketPulse.IntegrationTests --filter WatchlistApiTests`.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/MarketPulse.Infrastructure src/MarketPulse.Application src/MarketPulse.Api
git commit -m "feat(ingestion): MarketData options and source-switched composition skeleton"
```

---

### Task 2: YahooQuoteClient — fetch, parse, map (TDD)

**Files:**
- Create: `src/MarketPulse.Infrastructure/RealTime/YahooQuoteClient.cs`
- Create: `src/MarketPulse.Infrastructure/RealTime/YahooSymbols.cs`
- Create: `tests/MarketPulse.UnitTests/RealTime/YahooQuoteClientTests.cs`
- Create: `tests/MarketPulse.UnitTests/RealTime/Fixtures/yahoo-chart-ivv-ax.json` (embedded or copied content — match how the test project handles files; if no convention exists, embed the JSON as a C# raw string literal constant in the test file instead of a loose file)

**Interfaces:**
- Produces:
  - `YahooSymbols` — `static string ToYahoo(string asxCode)` (`"IVV"` → `"IVV.AX"`), `static string? ToAsx(string yahooSymbol)` (`"IVV.AX"` → `"IVV"`, null for anything without the `.AX` suffix).
  - `YahooQuoteClient` — ctor `(HttpClient http)`; `Task<decimal?> GetPriceAsync(string yahooSymbol, CancellationToken ct)` returning the parsed `regularMarketPrice`, or null for any non-success status, unparseable body, missing/non-positive price (the caller logs; this client stays silent and null — one seam, one job). Throws only on cancellation. Transport exceptions (`HttpRequestException`, timeout) propagate — the resilience pipeline (Task 3) and the poller's per-symbol catch (Task 4) own those.

- [ ] **Step 1: Write the failing tests**

The scripted-handler helper this file introduces is reused by Tasks 3–4 — write it as a small internal class in the test file:

```csharp
using System.Net;
using System.Text;
using MarketPulse.Infrastructure.RealTime;

namespace MarketPulse.UnitTests.RealTime;

/// <summary>Scripted HttpMessageHandler: each call pops the next response (or throws).</summary>
internal sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
    : HttpMessageHandler
{
    private int _calls;

    public int Calls => _calls;
    public List<Uri?> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri);
        var step = script[Math.Min(_calls, script.Length - 1)];
        _calls++;
        return Task.FromResult(step(request));
    }
}

public class YahooQuoteClientTests
{
    /// <summary>
    /// The v8 chart shape as Yahoo actually returns it (captured 2026-08, trimmed to the
    /// fields the client reads plus realistic noise it must ignore).
    /// </summary>
    private const string IvvChartJson = """
    {
      "chart": {
        "result": [
          {
            "meta": {
              "currency": "AUD",
              "symbol": "IVV.AX",
              "exchangeName": "ASX",
              "instrumentType": "ETF",
              "regularMarketPrice": 62.41,
              "regularMarketTime": 1754355600,
              "previousClose": 62.10
            },
            "timestamp": [1754355600],
            "indicators": { "quote": [ { "close": [62.41] } ] }
          }
        ],
        "error": null
      }
    }
    """;

    private static YahooQuoteClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://stub.local") });

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Parses_the_regular_market_price_from_a_real_chart_response()
    {
        var handler = new ScriptedHandler(_ => Json(IvvChartJson));

        var price = await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None);

        Assert.Equal(62.41m, price);
        Assert.Equal(
            "/v8/finance/chart/IVV.AX?interval=1d&range=1d",
            handler.Requests[0]!.PathAndQuery);
    }

    [Theory]
    [InlineData("""{ "chart": { "result": null, "error": { "code": "Not Found" } } }""")]
    [InlineData("""{ "chart": { "result": [], "error": null } }""")]
    [InlineData("""{ "chart": { "result": [ { "meta": { "symbol": "IVV.AX" } } ], "error": null } }""")]
    [InlineData("this is not json")]
    public async Task An_unparseable_or_priceless_body_yields_null(string body)
    {
        var handler = new ScriptedHandler(_ => Json(body));

        Assert.Null(await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public async Task A_non_positive_price_yields_null(decimal price)
    {
        var body = IvvChartJson.Replace("62.41", price.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        var handler = new ScriptedHandler(_ => Json(body));

        Assert.Null(await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None));
    }

    [Fact]
    public async Task A_non_success_status_yields_null_rather_than_throwing()
    {
        var handler = new ScriptedHandler(_ => Json("{}", HttpStatusCode.TooManyRequests));

        Assert.Null(await Client(handler).GetPriceAsync("IVV.AX", CancellationToken.None));
    }
}

public class YahooSymbolsTests
{
    [Fact]
    public void Maps_both_directions_and_rejects_foreign_symbols()
    {
        Assert.Equal("IVV.AX", YahooSymbols.ToYahoo("IVV"));
        Assert.Equal("IVV", YahooSymbols.ToAsx("IVV.AX"));
        Assert.Null(YahooSymbols.ToAsx("AAPL"));
        Assert.Null(YahooSymbols.ToAsx("IVV.NZ"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MarketPulse.UnitTests --filter "YahooQuoteClient|YahooSymbols"`
Expected: FAIL — types don't exist (compile RED).

- [ ] **Step 3: Implement**

`YahooSymbols.cs`:

```csharp
namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The seed tickers are bare ASX codes; Yahoo speaks exchange-suffixed symbols. One suffix,
/// two directions, no table — every seeded instrument is ASX by product definition.
/// </summary>
public static class YahooSymbols
{
    private const string AsxSuffix = ".AX";

    public static string ToYahoo(string asxCode) => $"{asxCode}{AsxSuffix}";

    public static string? ToAsx(string yahooSymbol) =>
        yahooSymbol.EndsWith(AsxSuffix, StringComparison.Ordinal)
            ? yahooSymbol[..^AsxSuffix.Length]
            : null;
}
```

`YahooQuoteClient.cs`:

```csharp
using System.Text.Json;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// One symbol, one request, one nullable price. The keyless v8 chart endpoint is the
/// stable unofficial surface (the batched v7 quote endpoint has been crumb-gated since
/// 2023 — see ADR-010). Anything that is not a positive price in a well-formed 200 is
/// null: the caller decides what a missing price means; transport failures propagate to
/// the resilience pipeline that owns them.
/// </summary>
public sealed class YahooQuoteClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<decimal?> GetPriceAsync(string yahooSymbol, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            $"/v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}?interval=1d&range=1d", ct);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        ChartResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<ChartResponse>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }

        var price = parsed?.Chart?.Result?.FirstOrDefault()?.Meta?.RegularMarketPrice;
        return price is > 0 ? price : null;
    }

    private sealed record ChartResponse(Chart? Chart);
    private sealed record Chart(IReadOnlyList<ChartResult>? Result);
    private sealed record ChartResult(Meta? Meta);
    private sealed record Meta(decimal? RegularMarketPrice);
}
```

(`ReadFromJsonAsync` needs `System.Net.Http.Json` — in the shared framework, no package. If record deserialization trips on the nested `Chart` property name clash with the record type, rename the records (`ChartEnvelope` etc.) — keep the JSON property names via `JsonSerializerDefaults.Web`'s case-insensitivity.)

- [ ] **Step 4: Run to verify pass, then the full unit suite**

Run: `dotnet test tests/MarketPulse.UnitTests --filter "YahooQuoteClient|YahooSymbols"` then the whole project.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MarketPulse.Infrastructure/RealTime tests/MarketPulse.UnitTests/RealTime
git commit -m "feat(ingestion): keyless Yahoo v8 chart client with strict parse-or-null semantics"
```

---

### Task 3: The resilience pipeline (TDD)

**Files:**
- Create: `src/MarketPulse.Infrastructure/RealTime/MarketDataResilience.cs` (the pipeline definition, callable both from DI registration and directly from tests)
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (register the typed client with the pipeline — inert until Task 4 hosts the service)
- Create: `tests/MarketPulse.UnitTests/RealTime/MarketDataResilienceTests.cs`
- Modify: `tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj` (+ `Microsoft.Extensions.TimeProvider.Testing`, with its `PackageVersion` in `Directory.Packages.props`)

**Interfaces:**
- Produces:
  - `MarketDataResilience.Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder, MarketDataOptions options, TimeProvider timeProvider)` — static, so the exact production pipeline is testable without DI: retry (3 attempts, exponential backoff with jitter, on `HttpRequestException`/5xx/408/429), circuit breaker (opens after a failure-ratio window equivalent to ~2 failed polls once retries are exhausted, `BreakDuration` 2 minutes, samples on the same transient predicate), attempt timeout (`options.AttemptTimeout`).
  - DI: `services.AddHttpClient<YahooQuoteClient>(...)` with `BaseAddress` from options and `.AddResilienceHandler("market-data", (builder, context) => ...)` calling `Configure`.

- [ ] **Step 1: Write the failing tests**

Test the pipeline directly (deterministic, no DI, no real time). Key tool: `FakeTimeProvider` drives backoff and break duration; the scripted handler from Task 2 supplies failures.

```csharp
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;

namespace MarketPulse.UnitTests.RealTime;

public class MarketDataResilienceTests
{
    private static readonly MarketDataOptions Options = new()
    {
        AttemptTimeout = TimeSpan.FromMilliseconds(200),
        PollInterval = TimeSpan.FromSeconds(20)
    };

    private static (ResiliencePipeline<HttpResponseMessage> Pipeline, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider();
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = clock };
        MarketDataResilience.Configure(builder, Options, clock);
        return (builder.Build(), clock);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_within_one_execution()
    {
        var (pipeline, clock) = Build();
        var attempts = 0;

        var task = pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.CompletedTask;
            return attempts < 3
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK);
        }).AsTask();

        // Walk the fake clock past the backoff delays until the execution completes.
        while (!task.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        Assert.Equal(HttpStatusCode.OK, (await task).StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Sustained_failure_opens_the_breaker_and_fails_fast()
    {
        var (pipeline, clock) = Build();

        static ValueTask<HttpResponseMessage> AlwaysFail(ResilienceContext _) =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        // Enough failed executions to trip the breaker (each execution burns its retries).
        for (var i = 0; i < 4; i++)
        {
            var task = pipeline.ExecuteAsync(AlwaysFail).AsTask();
            while (!task.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
            _ = await task; // 500 back, or BrokenCircuitException once open — both fine here
        }

        // Now open: the next execution must fail fast without invoking the callback.
        var invoked = false;
        var afterOpen = pipeline.ExecuteAsync(_ =>
        {
            invoked = true;
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }).AsTask();
        while (!afterOpen.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        await Assert.ThrowsAsync<BrokenCircuitException>(() => afterOpen);
        Assert.False(invoked);
    }

    [Fact]
    public async Task The_breaker_half_opens_after_the_break_and_recovers_on_success()
    {
        var (pipeline, clock) = Build();

        static ValueTask<HttpResponseMessage> AlwaysFail(ResilienceContext _) =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        for (var i = 0; i < 4; i++)
        {
            var t = pipeline.ExecuteAsync(AlwaysFail).AsTask();
            while (!t.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }
            _ = await t;
        }

        // Ride out the break duration; the half-open probe should reach the callback.
        clock.Advance(TimeSpan.FromMinutes(3));

        var probe = pipeline.ExecuteAsync(_ =>
            ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK))).AsTask();
        while (!probe.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        Assert.Equal(HttpStatusCode.OK, (await probe).StatusCode);
    }

    [Fact]
    public async Task A_hung_attempt_is_cut_by_the_attempt_timeout()
    {
        var (pipeline, clock) = Build();

        var task = pipeline.ExecuteAsync(async context =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }).AsTask();

        while (!task.IsCompleted) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Yield(); }

        // Retries wrap the timeout: after 3 timed-out attempts the outcome is a timeout
        // exception (TimeoutRejectedException or TaskCanceled, per pipeline order).
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }
}
```

Implementation-time latitude, stated up front: Polly v8's exact exception surfacing (`TimeoutRejectedException` vs `BrokenCircuitException` timing, `FakeTimeProvider` interplay with `Task.Yield` loops) is fiddly — the *behavioural contracts* above are the requirement (retry count, fail-fast without callback invocation, half-open recovery, hung-attempt cut). Adjust assertion specifics to the real exception types you observe, keep the contracts, and record any adjustment in the report. If a test proves genuinely non-deterministic under `FakeTimeProvider`, that's a stop-and-report, not a `Task.Delay` sprinkle.

- [ ] **Step 2: Run to verify failure, then implement**

`MarketDataResilience.cs`:

```csharp
using System.Net;
using MarketPulse.Application.Configuration;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The pipeline ADR-010 describes, defined once and testable without DI. Order matters:
/// retry outermost (a retry may span a broken-then-recovered circuit), breaker inside it,
/// per-attempt timeout innermost so one hung request can never stack polls.
/// </summary>
public static class MarketDataResilience
{
    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        MarketDataOptions options,
        TimeProvider timeProvider,
        ILogger? logger = null)
    {
        builder.TimeProvider = timeProvider;

        static bool IsTransient(Outcome<HttpResponseMessage> outcome) =>
            outcome.Exception is HttpRequestException or TimeoutRejectedException
            || outcome.Result?.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or >= HttpStatusCode.InternalServerError;

        builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(250),
            ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome))
        });

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            // Tuned so roughly two consecutive fully-retried failed polls open it: the
            // sampling window spans a few polls, and a 0.9 ratio over >=6 samples means
            // sustained failure, not one bad batch.
            FailureRatio = 0.9,
            MinimumThroughput = 6,
            SamplingDuration = TimeSpan.FromSeconds(90),
            BreakDuration = TimeSpan.FromMinutes(2),
            ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),

            // The spec's "once, not per poll" logging: state transitions announce
            // themselves here; the per-symbol path logs breaker fail-fasts at Debug.
            OnOpened = args =>
            {
                logger?.LogWarning(
                    "Market-data circuit opened for {Break} after sustained failures.",
                    args.BreakDuration);
                return ValueTask.CompletedTask;
            },
            OnClosed = _ =>
            {
                logger?.LogInformation("Market-data circuit closed; polls resume.");
                return ValueTask.CompletedTask;
            }
        });

        builder.AddTimeout(new TimeoutStrategyOptions { Timeout = options.AttemptTimeout });
    }
}
```

DI in `AddInfrastructure` (inert until Task 4 — the client registration is harmless beside the fake):

```csharp
        services.AddHttpClient<YahooQuoteClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
        })
        .AddResilienceHandler("market-data", (builder, context) =>
        {
            var options = context.ServiceProvider
                .GetRequiredService<IOptions<MarketDataOptions>>().Value;
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>().CreateLogger("MarketPulse.MarketData.Resilience");
            MarketDataResilience.Configure(builder, options, TimeProvider.System, logger);
        });
```

(The worker host never binds `MarketDataOptions` — but `AddInfrastructure` is API-only (`AddPersistence` is the worker's entry), so no worker change. Verify that claim against `MarketPulse.Alerts/Program.cs` before relying on it; it was true at planning time.)

- [ ] **Step 3: Run to verify pass, full unit suite, build**

Run: `dotnet test tests/MarketPulse.UnitTests --filter MarketDataResilience`, then the full unit project, then `dotnet build MarketPulse.sln`.

- [ ] **Step 4: Commit**

```bash
git add Directory.Packages.props src/MarketPulse.Infrastructure tests/MarketPulse.UnitTests
git commit -m "feat(ingestion): declared retry-breaker-timeout pipeline, tested against a fake clock"
```

---

### Task 4: YahooPriceFeedService and the composition switch (TDD)

**Files:**
- Create: `src/MarketPulse.Infrastructure/RealTime/YahooPriceFeedService.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (replace the Task-1 `NotSupportedException` with the real registration)
- Create: `tests/MarketPulse.UnitTests/RealTime/YahooPriceFeedServiceTests.cs`
- Create: `tests/MarketPulse.IntegrationTests/MarketDataCompositionTests.cs`

**Interfaces:**
- Consumes: `YahooQuoteClient`, `YahooSymbols`, `PriceTickChannel`, `SeedData.ReferenceTickers` (codes), `MarketDataOptions`.
- Produces: `YahooPriceFeedService` — ctor `(YahooQuoteClient client, PriceTickChannel channel, IOptions<MarketDataOptions> options, ILogger<YahooPriceFeedService> logger, TimeProvider timeProvider)` (TimeProvider for the poll timer so unit tests drive polls without real waits; DI resolves `TimeProvider.System` — register it if not already registered).

- [ ] **Step 1: Write the failing unit tests**

Test through the real service with the scripted handler (real `YahooQuoteClient`), `FakeTimeProvider` driving `PeriodicTimer`, reading ticks off a real `PriceTickChannel`:

Facts to cover (write them in the house sentence style; construct the service directly):
1. `A_poll_writes_one_tick_per_seeded_symbol` — script per-symbol 200s with distinct prices; advance the clock one interval; read the channel: one tick per seed code, ASX codes (not `.AX`), positive prices, `TimestampUtc` = the fake clock's now (observation time).
2. `A_bad_quote_is_skipped_while_siblings_tick` — one symbol scripted to 500 (after pipeline exhaustion this surfaces as null/exception — the service catches per symbol); others 200; the channel gets N−1 ticks and one warning is logged (assert via a test logger or accept the count-only assertion and note it).
3. `A_failed_poll_writes_nothing_and_the_next_one_self_heals` — all symbols fail on poll 1, all succeed on poll 2; channel empty after poll 1, N ticks after poll 2.
4. `Stopping_the_service_completes_promptly` — `StopAsync` within a bounded wait (the BrokerOutageTests shutdown-contract pattern).

Wire the resilience pipeline into these tests ONLY if it doesn't fight the fake clock (the pipeline has its own time). Simpler and sanctioned: unit-test the service with a plain `HttpClient` over the scripted handler (no pipeline — Task 3 already proved the pipeline), and let the per-symbol catch in the service handle the scripted failures. State in the test file's header comment that the pipeline is composed in DI and tested separately.

- [ ] **Step 2: Implement the service**

```csharp
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The real tick producer: one keyless chart request per seeded symbol per poll, written
/// to the same channel the fake fills. Failure semantics are 4a's, unchanged: ticks are
/// lossy by design — a failed symbol is skipped with one warning, a failed poll writes
/// nothing, the next poll self-heals, and when the upstream is truly down the breaker
/// (composed in DI) makes the failures cheap while the dashboard's staleness UI tells the
/// truth. There is deliberately no fallback to the fake: invented prices presented as real
/// ones is the one failure mode this slice refuses (ADR-010).
/// </summary>
public sealed class YahooPriceFeedService(
    YahooQuoteClient client,
    PriceTickChannel channel,
    IOptions<MarketDataOptions> options,
    ILogger<YahooPriceFeedService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var codes = SeedData.ReferenceTickers.Select(t => t.Code).ToArray();
        logger.LogInformation(
            "YahooPriceFeedService polling {Count} symbols every {Interval}.",
            codes.Length, options.Value.PollInterval);

        using var timer = new PeriodicTimer(options.Value.PollInterval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PollOnceAsync(codes, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("YahooPriceFeedService stopping.");
        }
    }

    private async Task PollOnceAsync(string[] codes, CancellationToken ct)
    {
        // Concurrent within the poll, bounded by the symbol count (~8) — no throttling
        // machinery for a workload this small.
        var polls = codes.Select(async code =>
        {
            try
            {
                var price = await client.GetPriceAsync(YahooSymbols.ToYahoo(code), ct);

                if (price is not { } value)
                {
                    logger.LogWarning("No usable quote for {Code}; skipping this poll.", code);
                    return;
                }

                await channel.Writer.WriteAsync(
                    new PriceTick(code, value, timeProvider.GetUtcNow()), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not failure.
            }
            catch (Polly.CircuitBreaker.BrokenCircuitException)
            {
                // The breaker already announced itself once at Warning (OnOpened); eight
                // symbols repeating it every poll would be noise. Debug keeps the trace.
                logger.LogDebug("Circuit open; skipped {Code} this poll.", code);
            }
            catch (Exception ex)
            {
                // Transport failure past the pipeline. One warning; the symbol misses one
                // poll. Lossy by design.
                logger.LogWarning(ex, "Quote request failed for {Code}; skipping this poll.", code);
            }
        });

        await Task.WhenAll(polls);
    }
}
```

(`PeriodicTimer(TimeSpan, TimeProvider)` exists on .NET 8+. If `SeedData` is `internal` to Infrastructure this compiles fine — the service is in the same assembly; verify.)

Composition switch — replace the Task-1 placeholder:

```csharp
        if (string.Equals(source, MarketDataOptions.YahooSource, StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<YahooPriceFeedService>();
        }
        else
        {
            services.AddHostedService<FakeTickService>();
        }
```

with `services.AddSingleton(TimeProvider.System);` beside it if `TimeProvider` isn't already registered (use TryAddSingleton).

- [ ] **Step 3: Write the composition integration tests**

`MarketDataCompositionTests.cs`, `[Collection(nameof(SqlServerCollection))]` — build `TestFactory.Create(fixture, overrides)` and assert against `factory.Services.GetServices<IHostedService>()`:
1. Default (no override) → contains `FakeTickService`, not `YahooPriceFeedService`.
2. `["MarketData:Source"] = "Yahoo"` → contains `YahooPriceFeedService`, not `FakeTickService`. (The service will fail its first real poll against the real BaseUrl inside TestServer — but polls only start after the interval and log-and-continue; asserting registration doesn't await polls. To be safe, also override `MarketData:PollInterval` to something long, e.g. `"01:00:00"`, so no poll fires during the test.)
3. `["MarketData:Source"] = "Chaos"` → factory creation (first server access) throws an options validation error naming `Source`.

- [ ] **Step 4: Run everything**

Run: new unit + composition tests; then the FULL unit + integration suites; then `pnpm -r test -- --run` and the e2e suite once (`pkill -f MarketPulse.Alerts; pnpm --dir tests/e2e exec playwright test`) — all existing suites must be green with zero changes to their code.

- [ ] **Step 5: Commit**

```bash
git add src/MarketPulse.Infrastructure tests/MarketPulse.UnitTests tests/MarketPulse.IntegrationTests
git commit -m "feat(ingestion): Yahoo price feed hosted by configuration, fake stays default"
```

---

### Task 5: ADR-010 and docs reconciliation

**Files:**
- Create: `docs/adr/010-market-data-feed.md`
- Modify: `README.md`, `docs/MarketPulse-Pro-README.md` (the two "slice 6, not yet built" resilience claims; the FakeTickService references)
- Modify: `docs/ROADMAP.md` (slice 6 done; phase-2 row; verification anchor)
- Modify: `docs/TESTING.md` (the resilience unit tests incl. the fake-clock breaker tests; composition tests; the no-live-calls rule)

- [ ] **Step 1: Write ADR-010**

Mirror ADR-009/007 conventions. Must carry: the Yahoo decision with keyed/paid providers rejected; the crumb-gated batch endpoint rejected for the per-symbol v8 chart surface (the spec amendment's story); **the tolerated-failure-modes table** (per-symbol bad quote → skip one tick; transient failure → retry inside the poll; sustained failure → breaker open, polls fail fast, staleness surfaces; upstream shape change → parse-to-null, same as bad quote; rate limiting → 429 is transient, backoff handles it); the **no-fallback decision** stated as a product decision; the cadence arithmetic; the rule-cache deferral cross-referencing ADR-009 with the fewer-ticks-than-the-fake evidence.

- [ ] **Step 2: Reconcile the other docs**

`grep -n "slice 6, not yet built\|FakeTickService" README.md docs/MarketPulse-Pro-README.md` — every hit updated to the built reality (the fake still exists and is the default for dev/tests — say that honestly rather than deleting its mentions). ROADMAP slice-6 section → done summary; phase-2 row updated; verification anchor refreshed. TESTING.md gains the resilience/composition test sections and states the no-live-Yahoo-calls-in-CI rule.

- [ ] **Step 3: Verify and commit**

Re-read edited sections; run the grep and confirm every hit.

```bash
git add docs README.md
git commit -m "docs: ADR-010 market-data feed — resilience claims now point at built code"
```

---

### Task 6: Full verification and the manual rehearsal

- [ ] **Step 1: Clean-slate build and test, every suite**

```bash
pnpm -r build && pnpm -r test -- --run
dotnet build MarketPulse.sln && dotnet test
pkill -f MarketPulse.Alerts 2>/dev/null; pnpm --dir tests/e2e exec playwright test
```

Expected: everything green; zero modifications to any pre-existing test file anywhere in the branch (verify: `git diff --stat test..HEAD -- tests/ | grep -v "MarketData\|Yahoo\|Resilience"` shows nothing pre-existing changed).

- [ ] **Step 2: The manual rehearsal (spec done criterion 2 — actually rehearse it)**

With compose infra up: `MarketData__Source=Yahoo dotnet run --project src/MarketPulse.Api` (plus the worker if watching an alert fire), open the dashboard, and record in the report: real delayed ASX prices ticking on the watchlist (values plausibly near real quotes, not the seed random walk); an `above` alert set below a live price firing through the real pipeline; then sever the network (or point `MarketData:BaseUrl` at a dead port and restart) and record prices going stale with the breaker's state-change warning in the logs. **If the live endpoint's response shape differs from the Task-2 fixture, fix the DTOs/fixture to reality, re-run the suites, and record the delta — the fixture's authority is the live endpoint, not this plan.** If the environment genuinely cannot reach Yahoo (offline, blocked), report that criterion BLOCKED with what was attempted — do not fabricate evidence and do not substitute a stubbed run.

- [ ] **Step 3: Spec done-criteria walkthrough and finish**

Check all five done criteria with named evidence (superpowers:verification-before-completion); `git log --format=%B test..HEAD | grep -ic co-authored` → 0. Then superpowers:finishing-a-development-branch — merge into `test` per the promotion flow.
