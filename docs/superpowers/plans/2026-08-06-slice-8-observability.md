# Slice 8 — Observability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** OTel traces/metrics + Serilog structured logs from both hosts to a local Aspire dashboard, a bounded redelivery loop in `AlertTriggeredConsumer`, real readiness checks, security events that finally emit, and browser→worker correlation.

**Architecture:** Serilog bootstraps in each host's `Program.cs` (console JSON + OTLP), application code keeps `ILogger<T>`. OpenTelemetry SDK in both hosts exports OTLP to `Otel:OtlpEndpoint`. A BCL-only `Telemetry` class (Application layer) owns the `MarketPulse` Meter and the `MarketPulse.Messaging` ActivitySource; `traceparent` crosses RabbitMQ by hand. The transient requeue path becomes republish-with-`x-retry-count` → DLQ at `RetryLimit`, extracted into a testable `TransientRetry` helper.

**Tech Stack:** Serilog 9.x, OpenTelemetry .NET 1.12.x, Aspire dashboard container, RabbitMQ.Client 7, xUnit + NSubstitute + `MeterListener`/`ActivityListener` + `FakeLogger`.

**Spec:** `docs/superpowers/specs/2026-08-06-observability-design.md` — binding; read it first.

## Global Constraints

- Build ends **0 warnings** (`TreatWarningsAsErrors`); central package management (versions only in `Directory.Packages.props`).
- Conventional commits, one change each, **no Co-Authored-By trailer**; TDD per task.
- Branch: `feature/slice-8-observability`, merged to `test` only after the full gate.
- Serilog APIs appear ONLY in the two `Program.cs` files; everything else uses `ILogger<T>`/`BeginScope`. OpenTelemetry SDK packages only in host projects; Infrastructure/Application may reference `OpenTelemetry.Api` and BCL diagnostics only. `Domain` gains nothing (`DependencyRuleTests`).
- `/health` behaviour must not change (Playwright polls it); readiness lives at `/health/ready`.
- Metric instrument names exactly as defined in Task 1 — later tasks must use the `Telemetry` fields, never re-create instruments.
- Existing integration tests must stay green: no collector runs in CI, so telemetry must be inert when the endpoint is unreachable.
- Known pre-existing flaky tests under heavy parallel load (unrelated): `MarketDataResilienceTests`, `RabbitMqConsumerServiceTests` — if one flakes in a full run, re-run its filter once and report both outcomes.

## File Structure

```
Directory.Packages.props                              (modify — new pins)
src/MarketPulse.Application/
  Configuration/OtelOptions.cs                        (new)
  Telemetry/Telemetry.cs                              (new — Meter + ActivitySource + 9 counters)
  Authentication/EmailMasking.cs                      (new)
  Authentication/LoginCommand.cs                      (modify — events + counters)
  Authentication/RefreshSessionCommand.cs             (modify — reuse-detection event)
src/MarketPulse.Infrastructure/
  Messaging/MessagingTelemetry.cs                     (new — inject/extract traceparent)
  Messaging/RabbitMqEventPublisher.cs                 (modify — producer span)
  Messaging/RabbitMqTickSink.cs                       (modify — producer span)
  History/TickPersistenceService.cs                   (modify — persisted counter)
  History/TickBuffer.cs                               (modify — drop counter)
src/MarketPulse.Api/
  Program.cs                                          (modify — Serilog, OTel, health)
  Middleware/CorrelationIdMiddleware.cs               (modify — scope + Activity tag)
  Messaging/TransientRetry.cs                         (new)
  Messaging/AlertTriggeredConsumer.cs                 (modify — consumer span + retry call)
  Health/RabbitMqHealthCheck.cs                       (new)
src/MarketPulse.Alerts/
  Program.cs                                          (modify — Serilog, OTel)
  PriceConsumer.cs                                    (modify — consumer span)
  AlertEvaluator.cs                                   (modify — evaluated/triggered counters)
src/MarketPulse.Application/Configuration/RabbitMqOptions.cs (modify — RetryLimit)
packages/api-client/src/client.ts                     (modify — X-Correlation-Id)
docker-compose.yml                                    (modify — aspire-dashboard)
docs/adr/009-messaging-architecture.md                (modify — bound recorded)
docs/ROADMAP.md                                       (modify — closing commit)
tests: UnitTests/{Telemetry,Api,Application,Messaging}, IntegrationTests/HealthChecksTests.cs,
       IntegrationTests/TransientRetryTests.cs, api-client client.test.ts
```

---

### Task 1: Packages, OtelOptions, and the Telemetry class

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/MarketPulse.Api/MarketPulse.Api.csproj`, `src/MarketPulse.Alerts/MarketPulse.Alerts.csproj`, `src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj`, `tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj`
- Create: `src/MarketPulse.Application/Configuration/OtelOptions.cs`
- Create: `src/MarketPulse.Application/Telemetry/Telemetry.cs`
- Test: `tests/MarketPulse.UnitTests/Telemetry/TelemetryTests.cs`

**Interfaces:**
- Produces (every later task consumes these EXACT names): `Telemetry.MeterName = "MarketPulse"`, `Telemetry.MessagingSourceName = "MarketPulse.Messaging"`, and static `Counter<long>` fields `TicksPersisted` (`marketpulse.ticks.persisted`), `TickBufferDrops` (`marketpulse.ticks.buffer_drops`), `AlertsEvaluated` (`marketpulse.alerts.evaluated`), `AlertsTriggered` (`marketpulse.alerts.triggered`), `NotificationRedeliveries` (`marketpulse.notifications.redeliveries`), `NotificationDeadLetters` (`marketpulse.notifications.dead_letters`), `AuthLoginFailures` (`marketpulse.auth.login_failures`), `AuthLockouts` (`marketpulse.auth.lockouts`), `AuthRefreshReuse` (`marketpulse.auth.refresh_reuse_detected`); `OtelOptions { SectionName = "Otel"; string OtlpEndpoint = "http://localhost:4317" }`.

- [ ] **Step 1: Branch**

```bash
git checkout test && git pull && git checkout -b feature/slice-8-observability
```

- [ ] **Step 2: Pin packages**

Add to `Directory.Packages.props` (alphabetical within the group). These versions are believed current-stable; if `dotnet restore` returns NU1102 (version not found), substitute the latest stable the feed offers and record the substitution in your report — except `OpenTelemetry.Instrumentation.SqlClient`, which may only exist as a beta: pin the latest beta and note it.

```xml
<PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="10.0.0" />
<PackageVersion Include="Microsoft.Extensions.Diagnostics.Testing" Version="9.8.0" />
<PackageVersion Include="OpenTelemetry.Api" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.12.0" />
<PackageVersion Include="OpenTelemetry.Instrumentation.SqlClient" Version="1.12.0-beta.2" />
<PackageVersion Include="Serilog.AspNetCore" Version="9.0.0" />
<PackageVersion Include="Serilog.Extensions.Hosting" Version="9.0.0" />
<PackageVersion Include="Serilog.Sinks.OpenTelemetry" Version="4.2.0" />
```

Project references (no versions): Api gets `Serilog.AspNetCore`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Instrumentation.SqlClient`, `Serilog.Sinks.OpenTelemetry`, `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`. Alerts gets `Serilog.Extensions.Hosting`, `Serilog.Sinks.OpenTelemetry`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`, `OpenTelemetry.Instrumentation.SqlClient`. Infrastructure gets `OpenTelemetry.Api` only. UnitTests gets `Microsoft.Extensions.Diagnostics.Testing`.

- [ ] **Step 3: Write the failing test**

`tests/MarketPulse.UnitTests/Telemetry/TelemetryTests.cs`:

```csharp
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
            if (instrument.Meter.Name == Application.Telemetry.Telemetry.MeterName
                && instrument.Name == "marketpulse.auth.login_failures")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => observed += value);
        listener.Start();

        Application.Telemetry.Telemetry.AuthLoginFailures.Add(1);

        listener.RecordObservableInstruments();
        Assert.Equal(1, observed);
    }
}
```

- [ ] **Step 4: Run to verify it fails** (type missing)

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TelemetryTests"
```

- [ ] **Step 5: Implement**

`src/MarketPulse.Application/Telemetry/Telemetry.cs`:

```csharp
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
```

`src/MarketPulse.Application/Configuration/OtelOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

public sealed class OtelOptions
{
    public const string SectionName = "Otel";

    /// <summary>OTLP/gRPC endpoint. The Aspire dashboard listens here in docker-compose.</summary>
    [Required]
    public string OtlpEndpoint { get; init; } = "http://localhost:4317";
}
```

- [ ] **Step 6: Run to verify green + architecture intact**

```bash
dotnet build -c Release && dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TelemetryTests|FullyQualifiedName~DependencyRuleTests"
```

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(otel): telemetry instruments, OtelOptions, package pins"
```

---

### Task 2: API host — Serilog, OTel, correlation enrichment, Aspire in compose

**Files:**
- Modify: `src/MarketPulse.Api/Program.cs`
- Modify: `src/MarketPulse.Api/Middleware/CorrelationIdMiddleware.cs`
- Modify: `src/MarketPulse.Api/appsettings.json` + `appsettings.Development.json`
- Modify: `docker-compose.yml`
- Test: `tests/MarketPulse.UnitTests/Api/CorrelationIdMiddlewareTests.cs` (create or extend if exists — check first)

**Interfaces:**
- Consumes: `Telemetry.MeterName`/`MessagingSourceName`, `OtelOptions` (Task 1).
- Produces: the API exports OTLP; every request's logs carry `CorrelationId` via scope; `Activity.Current` carries tag `correlation.id`. Task 8 adds health mappings to the same `Program.cs`.

- [ ] **Step 1: Write the failing middleware test**

`tests/MarketPulse.UnitTests/Api/CorrelationIdMiddlewareTests.cs` (if a file for this middleware already exists, add these tests to it):

```csharp
using System.Diagnostics;
using MarketPulse.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Testing;

namespace MarketPulse.UnitTests.Api;

public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Tags_the_current_activity_and_opens_a_log_scope()
    {
        using var activity = new Activity("test").Start();
        var logger = new FakeLogger<CorrelationIdMiddleware>();
        string? scopedCorrelation = null;

        var middleware = new CorrelationIdMiddleware(context =>
        {
            // Prove the scope is open while downstream runs: log and inspect the scope.
            logger.LogInformation("inside");
            scopedCorrelation = context.Items[CorrelationIdMiddleware.HeaderName] as string;
            return Task.CompletedTask;
        }, logger);

        var http = new DefaultHttpContext();
        http.Request.Headers[CorrelationIdMiddleware.HeaderName] = "corr-123";

        await middleware.InvokeAsync(http);

        Assert.Equal("corr-123", scopedCorrelation);
        Assert.Equal("corr-123", activity.GetTagItem("correlation.id"));
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Contains(record.Scopes, s =>
            s is IReadOnlyList<KeyValuePair<string, object?>> kvs
            && kvs.Any(kv => kv.Key == "CorrelationId" && (string?)kv.Value == "corr-123"));
    }
}
```

- [ ] **Step 2: Run to verify it fails** (middleware has no logger parameter yet)

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~CorrelationIdMiddlewareTests"
```

- [ ] **Step 3: Extend the middleware**

`CorrelationIdMiddleware.cs` becomes:

```csharp
using System.Diagnostics;

namespace MarketPulse.Api.Middleware;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied)
                ? supplied.ToString()
                : Guid.NewGuid().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        // The ID finally lands somewhere searchable: on the trace and on every log line.
        Activity.Current?.SetTag("correlation.id", correlationId);

        using var scope = logger.BeginScope(
            new Dictionary<string, object?> { ["CorrelationId"] = correlationId });

        await next(context);
    }
}
```

(`UseMiddleware<CorrelationIdMiddleware>` resolves the logger automatically — no registration change.)

- [ ] **Step 4: Wire Serilog + OTel in `Program.cs`**

At the very top, before `var builder`: nothing. After `var builder = WebApplication.CreateBuilder(args);` add:

```csharp
var otlpEndpoint = builder.Configuration[$"{OtelOptions.SectionName}:OtlpEndpoint"]
    ?? "http://localhost:4317";

// Serilog is bootstrap-only: application code stays on ILogger<T>. Console gets compact
// JSON; OTLP carries the same structured events (scope properties included) to the
// Aspire dashboard. When no collector listens, the sink drops batches quietly.
builder.Host.UseSerilog((context, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .WriteTo.OpenTelemetry(o =>
    {
        o.Endpoint = otlpEndpoint;
        o.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = "marketpulse-api" };
    }));

builder.Services.AddOptions<OtelOptions>()
    .Bind(builder.Configuration.GetSection(OtelOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("marketpulse-api"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o =>
            o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddHttpClientInstrumentation()
        .AddSqlClientInstrumentation()
        .AddSource(MarketPulse.Application.Telemetry.Telemetry.MessagingSourceName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(MarketPulse.Application.Telemetry.Telemetry.MeterName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));
```

And immediately after `app.UseMiddleware<CorrelationIdMiddleware>();` (find the exact line) add:

```csharp
app.UseSerilogRequestLogging();
```

Add `using Serilog;` and `using MarketPulse.Application.Configuration;` (if missing) and `using OpenTelemetry.Metrics; using OpenTelemetry.Resources; using OpenTelemetry.Trace;`.

`appsettings.json` — add a top-level section:

```json
"Otel": { "OtlpEndpoint": "http://localhost:4317" },
"Serilog": { "MinimumLevel": { "Default": "Information", "Override": { "Microsoft.AspNetCore": "Warning", "Microsoft.EntityFrameworkCore": "Warning" } } }
```

(There may be an existing `Logging` section — leave it; Serilog ignores it. If integration tests assert on specific log output, run the full integration suite before committing and adapt only if something breaks — report any such adaptation.)

`docker-compose.yml` — add beside the existing services, matching indent style:

```yaml
  aspire-dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:9.1
    environment:
      DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS: "true"
    ports:
      - "18888:18888" # UI
      - "4317:18889"  # OTLP/gRPC in
```

- [ ] **Step 5: Verify — unit test green, API boots in tests with no collector**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~CorrelationIdMiddlewareTests"
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~WatchlistApiTests|FullyQualifiedName~HistoryOptionsTests"
```

Expected: PASS — the second command proves the host boots and serves with telemetry configured and no collector running.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(otel): API host — Serilog + OpenTelemetry to OTLP, correlation enrichment, Aspire in compose"
```

---

### Task 3: Worker host — Serilog + OTel

**Files:**
- Modify: `src/MarketPulse.Alerts/Program.cs`

**Interfaces:**
- Consumes: Task 1's names. Produces: worker exports OTLP as `marketpulse-alerts`.

- [ ] **Step 1: Wire it** (worker uses `HostApplicationBuilder`; `AddSerilog` on services is the non-web equivalent of `UseSerilog`)

After the existing options binding in `src/MarketPulse.Alerts/Program.cs`:

```csharp
var otlpEndpoint = builder.Configuration[$"{OtelOptions.SectionName}:OtlpEndpoint"]
    ?? "http://localhost:4317";

builder.Services.AddSerilog(loggerConfiguration => loggerConfiguration
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .WriteTo.OpenTelemetry(o =>
    {
        o.Endpoint = otlpEndpoint;
        o.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = "marketpulse-alerts" };
    }));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("marketpulse-alerts"))
    .WithTracing(t => t
        .AddHttpClientInstrumentation()
        .AddSqlClientInstrumentation()
        .AddSource(MarketPulse.Application.Telemetry.Telemetry.MessagingSourceName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(m => m
        .AddRuntimeInstrumentation()
        .AddMeter(MarketPulse.Application.Telemetry.Telemetry.MeterName)
        .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint)));
```

Add the usings (`Serilog`, `OpenTelemetry.Metrics`, `OpenTelemetry.Resources`, `OpenTelemetry.Trace`). Add an `"Otel"` + `"Serilog"` section to `src/MarketPulse.Alerts/appsettings.json` mirroring Task 2's.

- [ ] **Step 2: Verify — solution builds 0 warnings; the worker's composition still validates**

```bash
dotnet build -c Release
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~AlertPipelineTests"
```

(The alert-pipeline tests boot the worker's consumers against the broker fixture — they are the composition proof.)

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(otel): worker host — Serilog + OpenTelemetry as marketpulse-alerts"
```

---

### Task 4: Trace context across the broker

**Files:**
- Create: `src/MarketPulse.Infrastructure/Messaging/MessagingTelemetry.cs`
- Modify: `src/MarketPulse.Infrastructure/Messaging/RabbitMqEventPublisher.cs`
- Modify: `src/MarketPulse.Infrastructure/Messaging/RabbitMqTickSink.cs`
- Modify: `src/MarketPulse.Api/Messaging/AlertTriggeredConsumer.cs`
- Modify: `src/MarketPulse.Alerts/PriceConsumer.cs`
- Test: `tests/MarketPulse.UnitTests/Messaging/MessagingTelemetryTests.cs`

**Interfaces:**
- Produces: `MessagingTelemetry.StartProducerActivity(string exchange, BasicProperties properties): Activity?` (injects `traceparent` into `properties.Headers`) and `MessagingTelemetry.StartConsumerActivity(string queue, IReadOnlyBasicProperties properties): Activity?` (extracts the remote parent). Task 5's republish must preserve incoming headers — including `traceparent`.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.UnitTests/Messaging/MessagingTelemetryTests.cs`:

```csharp
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
            ShouldListenTo = source => source.Name == Telemetry.MessagingSourceName,
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
```

- [ ] **Step 2: Run to verify they fail** (type missing)

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~MessagingTelemetryTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Infrastructure/Messaging/MessagingTelemetry.cs`:

```csharp
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
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

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
```

- [ ] **Step 4: Wire the four call sites**

In `RabbitMqEventPublisher.PublishAsync`, after the `properties` object is constructed and before `BasicPublishAsync`:

```csharp
using var activity = MessagingTelemetry.StartProducerActivity(_options.AlertsExchange, properties);
```

In `RabbitMqTickSink` — find its publish method, locate where its `BasicProperties` is constructed, and add the same one-liner with its exchange (`_options.PricesExchange` or the field the file actually uses) before its `BasicPublishAsync`.

In `AlertTriggeredConsumer.HandleAsync`, first line of the method body:

```csharp
using var activity = MessagingTelemetry.StartConsumerActivity(Options.NotificationsQueue, ea.BasicProperties);
```

In `PriceConsumer.HandleAsync`, first line of the method body:

```csharp
using var activity = MessagingTelemetry.StartConsumerActivity(Options.PricesQueue, ea.BasicProperties);
```

- [ ] **Step 5: Verify green + messaging integration intact**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~MessagingTelemetryTests"
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~AlertPipelineTests|FullyQualifiedName~OutboxDispatchTests"
```

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(otel): traceparent across the broker — producer/consumer spans on every message"
```

---

### Task 5: The redelivery bound

**Files:**
- Modify: `src/MarketPulse.Application/Configuration/RabbitMqOptions.cs`
- Create: `src/MarketPulse.Api/Messaging/TransientRetry.cs`
- Modify: `src/MarketPulse.Api/Messaging/AlertTriggeredConsumer.cs` (the transient `DbUpdateException` catch only)
- Modify: `docs/adr/009-messaging-architecture.md`
- Test: `tests/MarketPulse.UnitTests/Messaging/TransientRetryTests.cs`
- Test: `tests/MarketPulse.IntegrationTests/TransientRetryIntegrationTests.cs`

**Interfaces:**
- Consumes: `MessagingTelemetry` header conventions (Task 4 — incoming headers must be preserved on republish), `Telemetry.NotificationRedeliveries`/`NotificationDeadLetters`.
- Produces: `RabbitMqOptions.RetryLimit` (`[Range(1, 100)]`, default 5); `TransientRetry.ReadRetryCount(IReadOnlyBasicProperties): int`; `TransientRetry.RetryOrDeadLetterAsync(IChannel channel, BasicDeliverEventArgs ea, string queueName, int retryLimit, ILogger logger, CancellationToken ct): Task`.

- [ ] **Step 1: Add `RetryLimit`**

In `RabbitMqOptions`, after `PrefetchCount`:

```csharp
/// <summary>
/// Bound on the transient-failure redelivery loop (ADR-009): a message that fails
/// transiently this many times is dead-lettered instead of retried forever.
/// </summary>
[Range(1, 100)]
public int RetryLimit { get; init; } = 5;
```

- [ ] **Step 2: Write the failing unit tests**

`tests/MarketPulse.UnitTests/Messaging/TransientRetryTests.cs`:

```csharp
using System.Text;
using MarketPulse.Api.Messaging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.UnitTests.Messaging;

public class TransientRetryTests
{
    private static BasicDeliverEventArgs Delivery(IReadOnlyBasicProperties properties) =>
        new("tag", 7UL, false, "", "q", properties, new ReadOnlyMemory<byte>([1, 2, 3]));

    private static IReadOnlyBasicProperties Properties(IDictionary<string, object?>? headers)
    {
        var properties = Substitute.For<IReadOnlyBasicProperties>();
        properties.Headers.Returns(headers);
        properties.MessageId.Returns("m1");
        properties.CorrelationId.Returns("c1");
        properties.ContentType.Returns("application/json");
        return properties;
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(3, 3)]
    [InlineData(3L, 3)]
    public void ReadRetryCount_handles_absent_int_and_long(object? headerValue, int expected)
    {
        var headers = headerValue is null
            ? null
            : new Dictionary<string, object?> { ["x-retry-count"] = headerValue };

        Assert.Equal(expected, TransientRetry.ReadRetryCount(Properties(headers)));
    }

    [Fact]
    public async Task Below_the_cap_republishes_with_incremented_count_and_acks()
    {
        var channel = Substitute.For<IChannel>();
        var ea = Delivery(Properties(new Dictionary<string, object?>
        {
            ["x-retry-count"] = 2,
            ["traceparent"] = Encoding.UTF8.GetBytes("00-abc-def-01"),
        }));

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, "q", retryLimit: 5,
            new FakeLogger(), CancellationToken.None);

        await channel.Received(1).BasicPublishAsync(
            exchange: "",
            routingKey: "q",
            mandatory: true,
            basicProperties: Arg.Is<BasicProperties>(p =>
                (int)p.Headers!["x-retry-count"]! == 3
                && p.Headers!.ContainsKey("traceparent")
                && p.CorrelationId == "c1"),
            body: Arg.Any<ReadOnlyMemory<byte>>(),
            cancellationToken: Arg.Any<CancellationToken>());
        await channel.Received(1).BasicAckAsync(7UL, false, Arg.Any<CancellationToken>());
        await channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task At_the_cap_dead_letters()
    {
        var channel = Substitute.For<IChannel>();
        var ea = Delivery(Properties(new Dictionary<string, object?> { ["x-retry-count"] = 5 }));

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, "q", retryLimit: 5, new FakeLogger(), CancellationToken.None);

        await channel.Received(1).BasicNackAsync(7UL, false, false, Arg.Any<CancellationToken>());
        await channel.DidNotReceive().BasicPublishAsync(
            exchange: Arg.Any<string>(), routingKey: Arg.Any<string>(), mandatory: Arg.Any<bool>(),
            basicProperties: Arg.Any<BasicProperties>(), body: Arg.Any<ReadOnlyMemory<byte>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_republish_leaves_the_original_unacked()
    {
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                exchange: Arg.Any<string>(), routingKey: Arg.Any<string>(), mandatory: Arg.Any<bool>(),
                basicProperties: Arg.Any<BasicProperties>(), body: Arg.Any<ReadOnlyMemory<byte>>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("broker gone"));
        var ea = Delivery(Properties(null));

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, "q", retryLimit: 5, new FakeLogger(), CancellationToken.None);

        await channel.DidNotReceive().BasicAckAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
```

(NSubstitute argument syntax for `BasicPublishAsync` may need adjusting to the exact RabbitMQ.Client 7 signature — mirror how existing messaging unit tests substitute `IChannel`; look at `tests/MarketPulse.UnitTests/Messaging/` first. Keep the assertions' semantics identical.)

- [ ] **Step 3: Run to verify they fail**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TransientRetryTests"
```

- [ ] **Step 4: Implement**

`src/MarketPulse.Api/Messaging/TransientRetry.cs`:

```csharp
using System.Text;
using MarketPulse.Application.Telemetry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Api.Messaging;

/// <summary>
/// The bound ADR-009 deferred to this slice. A transient failure republishes the message
/// to the same queue with an incremented x-retry-count and acks the original; at the cap
/// it dead-letters. Republishing (rather than nack+requeue) is what makes the counter
/// possible — a requeued message arrives with identical headers, so nothing can count it.
/// The retries stay hot (broker-speed); the cap is what turns "forever" into "≤ RetryLimit".
/// </summary>
public static class TransientRetry
{
    public const string RetryCountHeader = "x-retry-count";

    public static int ReadRetryCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers is { } headers && headers.TryGetValue(RetryCountHeader, out var raw))
        {
            return raw switch
            {
                int i => i,
                long l => (int)l,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                _ => 0,
            };
        }

        return 0;
    }

    public static async Task RetryOrDeadLetterAsync(
        IChannel channel,
        BasicDeliverEventArgs ea,
        string queueName,
        int retryLimit,
        ILogger logger,
        CancellationToken ct)
    {
        var attempt = ReadRetryCount(ea.BasicProperties);

        if (attempt >= retryLimit)
        {
            Telemetry.NotificationDeadLetters.Add(1);
            logger.LogError(
                "Transient failure survived {Attempts} redeliveries; dead-lettering message {MessageId}.",
                attempt, ea.BasicProperties.MessageId);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        Telemetry.NotificationRedeliveries.Add(1);
        logger.LogWarning(
            "Transient failure handling message {MessageId}; redelivery {Attempt}/{Limit}.",
            ea.BasicProperties.MessageId, attempt + 1, retryLimit);

        // Preserve every incoming header (traceparent included) and overwrite the counter.
        var headers = ea.BasicProperties.Headers is { } incoming
            ? new Dictionary<string, object?>(incoming)
            : new Dictionary<string, object?>();
        headers[RetryCountHeader] = attempt + 1;

        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = ea.BasicProperties.MessageId,
            CorrelationId = ea.BasicProperties.CorrelationId,
            ContentType = ea.BasicProperties.ContentType,
            Headers = headers,
        };

        try
        {
            // Default exchange routes directly to the queue by name. Publish before ack:
            // if the publish fails, the original stays unacked and broker redelivery
            // takes over — the message is never lost between the two operations.
            await channel.BasicPublishAsync(
                exchange: "",
                routingKey: queueName,
                mandatory: true,
                basicProperties: properties,
                body: ea.Body,
                cancellationToken: ct);

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Failed to republish message {MessageId} for retry; leaving it unacked.",
                ea.BasicProperties.MessageId);
        }
    }
}
```

In `AlertTriggeredConsumer`, replace the transient catch's body — the existing:

```csharp
catch (DbUpdateException ex)
{
    // ... comment ...
    logger.LogWarning(ex, "Transient failure persisting a notification; requeueing.");
    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, ct);
}
```

becomes:

```csharp
catch (DbUpdateException ex)
{
    // The database is unreachable or otherwise unhappy — the alert is real and the fault
    // is ours, so retry. Bounded since slice 8 (ADR-009's deferred work): republish with
    // an incremented x-retry-count, dead-letter at RetryLimit. Dead-lettering *before*
    // the cap would lose exactly what the outbox exists to protect.
    logger.LogWarning(ex, "Transient failure persisting a notification; retrying.");
    await TransientRetry.RetryOrDeadLetterAsync(
        channel, ea, Options.NotificationsQueue, Options.RetryLimit, logger, ct);
}
```

- [ ] **Step 5: Write the integration test (real broker)**

`tests/MarketPulse.IntegrationTests/TransientRetryIntegrationTests.cs` — uses the existing `RabbitMqFixture` (read it first; mirror how `RabbitMqTopologyTests` opens channels and declares topology):

```csharp
using System.Text;
using MarketPulse.Api.Messaging;
using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(RabbitMqCollection))] // use the fixture's actual collection name
public class TransientRetryIntegrationTests(RabbitMqFixture fixture)
{
    [Fact]
    public async Task Below_cap_republish_lands_back_on_the_queue_with_incremented_header()
    {
        var options = fixture.CreateOptions(); // or however existing tests build RabbitMqOptions for the container
        await using var channel = await fixture.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);

        // Seed one message with x-retry-count = 1
        var seed = new BasicProperties
        {
            MessageId = "it-1",
            Headers = new Dictionary<string, object?> { [TransientRetry.RetryCountHeader] = 1 },
        };
        await channel.BasicPublishAsync("", options.NotificationsQueue, true, seed,
            Encoding.UTF8.GetBytes("{}"));

        var delivered = await BasicGetWithRetryAsync(channel, options.NotificationsQueue);
        var ea = new BasicDeliverEventArgs(
            "tag", delivered.DeliveryTag, false, "", options.NotificationsQueue,
            delivered.BasicProperties, delivered.Body);

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, options.NotificationsQueue, retryLimit: 5,
            NullLogger.Instance, CancellationToken.None);

        var redelivered = await BasicGetWithRetryAsync(channel, options.NotificationsQueue);
        Assert.Equal(2, TransientRetry.ReadRetryCount(redelivered.BasicProperties));
    }

    [Fact]
    public async Task At_cap_message_lands_in_the_dead_letter_queue()
    {
        var options = fixture.CreateOptions();
        await using var channel = await fixture.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);

        var seed = new BasicProperties
        {
            MessageId = "it-2",
            Headers = new Dictionary<string, object?> { [TransientRetry.RetryCountHeader] = 5 },
        };
        await channel.BasicPublishAsync("", options.NotificationsQueue, true, seed,
            Encoding.UTF8.GetBytes("{}"));

        var delivered = await BasicGetWithRetryAsync(channel, options.NotificationsQueue);
        var ea = new BasicDeliverEventArgs(
            "tag", delivered.DeliveryTag, false, "", options.NotificationsQueue,
            delivered.BasicProperties, delivered.Body);

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, options.NotificationsQueue, retryLimit: 5,
            NullLogger.Instance, CancellationToken.None);

        var dead = await BasicGetWithRetryAsync(channel, options.NotificationsDeadLetterQueue);
        Assert.Equal("it-2", dead.BasicProperties.MessageId);
    }

    private static async Task<BasicGetResult> BasicGetWithRetryAsync(IChannel channel, string queue)
    {
        for (var i = 0; i < 50; i++)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true);
            if (result is not null) return result;
            await Task.Delay(100);
        }

        throw new TimeoutException($"No message appeared on {queue} within 5s.");
    }
}
```

Adapt fixture member names (`CreateOptions`, `CreateChannelAsync`, collection name) to what `RabbitMqFixture` actually exposes — read it before writing; keep the test semantics identical. Note: the API project must be referenced by IntegrationTests already (it is — `WebApplicationFactory<Program>`), so `TransientRetry` is accessible.

- [ ] **Step 6: Run to verify green**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TransientRetryTests"
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~TransientRetryIntegrationTests"
```

- [ ] **Step 7: Amend ADR-009**

In `docs/adr/009-messaging-architecture.md`, find the consequence paragraph beginning "**The requeue loop has no bound until the observability slice adds one.**" and rewrite it to record: bounded in slice 8 via republish-with-`x-retry-count` and `RabbitMqOptions.RetryLimit` (default 5), dead-lettering at the cap, with redelivery/dead-letter metrics; the delayed-retry-queue remains the rejected (heavier) alternative; retries stay hot until the cap — the accepted trade. Keep the file's voice.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "fix(messaging): bound the transient redelivery loop — x-retry-count to DLQ at RetryLimit (ADR-009)"
```

---

### Task 6: Security events

**Files:**
- Create: `src/MarketPulse.Application/Authentication/EmailMasking.cs`
- Modify: `src/MarketPulse.Application/Authentication/LoginCommand.cs`
- Modify: `src/MarketPulse.Application/Authentication/RefreshSessionCommand.cs`
- Test: `tests/MarketPulse.UnitTests/Application/SecurityEventTests.cs` (new; existing handler tests must stay green)

**Interfaces:**
- Consumes: `Telemetry.AuthLoginFailures/AuthLockouts/AuthRefreshReuse` (Task 1).
- Produces: `EmailMasking.Mask(string email): string` — first character + `"***@"` + domain (`"a***@example.com"`); handlers emit warning events at the three sites.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.UnitTests/Application/SecurityEventTests.cs`:

```csharp
using MarketPulse.Application.Authentication;

namespace MarketPulse.UnitTests.Application;

public class SecurityEventTests
{
    [Theory]
    [InlineData("alice@example.com", "a***@example.com")]
    [InlineData("x@y.z", "x***@y.z")]
    [InlineData("weird", "w***")]
    public void Mask_never_reveals_the_local_part(string email, string expected) =>
        Assert.Equal(expected, EmailMasking.Mask(email));
}
```

Then extend the EXISTING login-handler unit tests (`tests/MarketPulse.UnitTests/Application/LoginHandlerTests.cs` — read it first; it drives the handler through NSubstitute doubles). Add, following its existing arrange helpers:

- a test that a failed login (wrong password path) logs a Warning containing the masked email and NOT the full email, using `FakeLogger<T>` (`Microsoft.Extensions.Logging.Testing`) injected as the handler's new `ILogger` parameter, and increments `marketpulse.auth.login_failures` (assert via a `MeterListener` scoped to the test, pattern from Task 1's `TelemetryTests`);
- a test that the lockout path increments `marketpulse.auth.lockouts`.

And in the refresh-handler tests (find `RefreshSessionHandlerTests` or equivalent): the reuse-detection path (presenting a revoked token) increments `marketpulse.auth.refresh_reuse_detected` and logs a Warning naming the user id.

(Metric assertions between parallel tests: `MeterListener` sees process-global instruments, so scope each assertion by measuring a delta — capture the count before acting and assert the difference — rather than asserting absolute totals.)

- [ ] **Step 2: Run to verify the new tests fail**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~SecurityEventTests|FullyQualifiedName~LoginHandlerTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Application/Authentication/EmailMasking.cs`:

```csharp
namespace MarketPulse.Application.Authentication;

/// <summary>Security events carry evidence, not credentials: first char + domain only.</summary>
public static class EmailMasking
{
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        var first = email.Length > 0 ? email[..1] : "?";
        return at > 0 ? $"{first}***{email[at..]}" : $"{first}***";
    }
}
```

In `LoginCommand.cs`'s handler: add `ILogger<LoginHandler> logger` to the primary constructor (namespace `Microsoft.Extensions.Logging`). At the site(s) that throw `InvalidCredentialsException` (both the unknown-email and wrong-password paths — read the handler; there may be one shared throw or two), add immediately before the throw:

```csharp
Telemetry.AuthLoginFailures.Add(1);
logger.LogWarning("Failed login attempt for {Email}.", EmailMasking.Mask(request.Email));
```

At the site that throws `AccountLockedException` (or where the lockout is recorded on the `User`):

```csharp
Telemetry.AuthLockouts.Add(1);
logger.LogWarning("Account locked after repeated failures: {Email}.", EmailMasking.Mask(request.Email));
```

In `RefreshSessionCommand.cs`'s handler, at the reuse-detection site (where the whole token family is revoked — the code path ADR-003/Q7.5 describes): add `ILogger<...>` similarly and:

```csharp
Telemetry.AuthRefreshReuse.Add(1);
logger.LogWarning("Refresh-token reuse detected for user {UserId}; revoking the token family.", token.UserId);
```

(Adapt identifier names — `request.Email`, `token.UserId` — to the handler's actual locals. `using MarketPulse.Application.Telemetry;` at the top. Existing tests construct these handlers directly: adding a constructor parameter WILL break their construction — pass `NullLogger<...>.Instance` or a `FakeLogger` in the existing arrange code, changing nothing else about those tests.)

- [ ] **Step 4: Run to verify green — new and pre-existing auth tests**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~SecurityEventTests|FullyQualifiedName~LoginHandlerTests|FullyQualifiedName~Refresh"
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~AuthApiTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(auth): security events — failed logins, lockouts, refresh reuse now emit"
```

---

### Task 7: Hot-path metrics

**Files:**
- Modify: `src/MarketPulse.Infrastructure/History/TickPersistenceService.cs`
- Modify: `src/MarketPulse.Infrastructure/History/TickBuffer.cs`
- Modify: `src/MarketPulse.Alerts/AlertEvaluator.cs`
- Test: extend `tests/MarketPulse.UnitTests/History/TickBufferTests.cs` and `tests/MarketPulse.UnitTests/History/TickPersistenceServiceTests.cs`

**Interfaces:**
- Consumes: `Telemetry.TicksPersisted/TickBufferDrops/AlertsEvaluated/AlertsTriggered`.

- [ ] **Step 1: Write the failing tests** (delta-measurement pattern from Task 6's note)

In `TickBufferTests`: overflow test grows an assertion that `marketpulse.ticks.buffer_drops` increased by the number of dropped ticks. In `TickPersistenceServiceTests`: the `Flush_writes_everything_buffered` test grows an assertion that `marketpulse.ticks.persisted` increased by 2. Use a small local helper in each file:

```csharp
private static long Measure(string instrument, Action act)
{
    long delta = 0;
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (inst, l) =>
    {
        if (inst.Meter.Name == MarketPulse.Application.Telemetry.Telemetry.MeterName
            && inst.Name == instrument)
        {
            l.EnableMeasurementEvents(inst);
        }
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        System.Threading.Interlocked.Add(ref delta, value));
    listener.Start();
    act();
    return System.Threading.Interlocked.Read(ref delta);
}
```

(For the async flush, use a `Func<Task>` overload — same body, `await act();`.)

- [ ] **Step 2: Verify the new assertions fail**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TickBufferTests|FullyQualifiedName~TickPersistenceServiceTests"
```

- [ ] **Step 3: Implement — one line per site**

- `TickBuffer` drop callback: add `Telemetry.TickBufferDrops.Add(1);` inside the `dropped =>` lambda (before or after the log line).
- `TickPersistenceService.FlushAsync`: after `await writer.WriteAsync(chunk, ct);` add `Telemetry.TicksPersisted.Add(chunk.Count);`.
- `AlertEvaluator.EvaluateAsync` (read the file): add `Telemetry.AlertsEvaluated.Add(1);` at entry, and `Telemetry.AlertsTriggered.Add(1);` at the site where a rule transitions to Triggered / an outbox row is written (one increment per triggered rule).
- `using MarketPulse.Application.Telemetry;` in each file.

- [ ] **Step 4: Verify green (unit + the alert evaluation integration tests)**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TickBufferTests|FullyQualifiedName~TickPersistenceServiceTests"
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~AlertEvaluationTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(otel): hot-path counters — ticks persisted, buffer drops, alerts evaluated/triggered"
```

---

### Task 8: Health checks

**Files:**
- Create: `src/MarketPulse.Api/Health/RabbitMqHealthCheck.cs`
- Modify: `src/MarketPulse.Api/Program.cs`
- Test: `tests/MarketPulse.UnitTests/Api/RabbitMqHealthCheckTests.cs`
- Test: `tests/MarketPulse.IntegrationTests/HealthChecksTests.cs`

**Interfaces:**
- Consumes: `RabbitMqConnection.CreateChannelAsync(bool, CancellationToken)` (existing seam), `MarketPulseDbContext`.
- Produces: `GET /health/ready` (anonymous) — 200 Healthy with dependencies up, 503 otherwise; `/health` byte-for-byte unchanged.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.UnitTests/Api/RabbitMqHealthCheckTests.cs`:

```csharp
using MarketPulse.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using NSubstitute;

namespace MarketPulse.UnitTests.Api;

public class RabbitMqHealthCheckTests
{
    [Fact]
    public async Task Healthy_when_a_channel_opens()
    {
        var channel = Substitute.For<IChannel>();
        var check = new RabbitMqHealthCheck((_, _) => Task.FromResult(channel));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        await channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Unhealthy_when_the_broker_refuses()
    {
        var check = new RabbitMqHealthCheck(
            (_, _) => Task.FromException<IChannel>(new InvalidOperationException("down")));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
```

`tests/MarketPulse.IntegrationTests/HealthChecksTests.cs`:

```csharp
using System.Net;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class HealthChecksTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Liveness_is_unchanged_and_dependency_free()
    {
        using var factory = TestFactory.Create(fixture);
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_reports_on_real_dependencies()
    {
        using var factory = TestFactory.Create(fixture);
        var response = await factory.CreateClient().GetAsync("/health/ready");

        // SQL (Testcontainers) is up. RabbitMQ may or may not be reachable in this
        // fixture — assert the endpoint exists and returns a health-check status code,
        // and that the body names both checks.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sqlserver", body);
        Assert.Contains("rabbitmq", body);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~RabbitMqHealthCheckTests"
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~HealthChecksTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Api/Health/RabbitMqHealthCheck.cs`:

```csharp
using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace MarketPulse.Api.Health;

/// <summary>Readiness = "can I open a channel right now". Same substitution seam as the
/// other messaging components: the single capability, not the sealed connection.</summary>
public sealed class RabbitMqHealthCheck(
    Func<bool, CancellationToken, Task<IChannel>> createChannel) : IHealthCheck
{
    public RabbitMqHealthCheck(RabbitMqConnection connection)
        : this(connection.CreateChannelAsync)
    {
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await createChannel(false, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot open a RabbitMQ channel.", ex);
        }
    }
}
```

In `Program.cs`, with the service registrations:

```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<MarketPulseDbContext>("sqlserver")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");
```

and beside the existing `/health` mapping (which gains only a comment: `// Liveness: dependency-free by design — Playwright and load balancers poll this.`):

```csharp
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
            }),
        });
    },
}).AllowAnonymous();
```

DI note: `RabbitMqHealthCheck`'s DI constructor takes the singleton `RabbitMqConnection` — `AddCheck<T>` activates it fine. Add `using MarketPulse.Api.Health;` and `using MarketPulse.Infrastructure.Persistence;`.

- [ ] **Step 4: Verify green**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~RabbitMqHealthCheckTests"
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~HealthChecksTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(health): /health/ready with SQL + RabbitMQ checks; /health stays liveness"
```

---

### Task 9: Browser correlation header

**Files:**
- Modify: `packages/api-client/src/client.ts`
- Test: `packages/api-client/src/client.test.ts`

- [ ] **Step 1: Write the failing test** (follow the file's fetch-mock style — see the existing tests' `fetchMock.mock.calls` header assertions)

```ts
it('every request carries a unique X-Correlation-Id', async () => {
  fetchMock.mockResolvedValue(jsonResponse({ id: 'w1', items: [] }));

  await client.getWatchlist();
  await client.getWatchlist();

  const headers1 = (fetchMock.mock.calls[0]?.[1] as RequestInit).headers as Record<string, string>;
  const headers2 = (fetchMock.mock.calls[1]?.[1] as RequestInit).headers as Record<string, string>;
  expect(headers1['X-Correlation-Id']).toBeTruthy();
  expect(headers2['X-Correlation-Id']).toBeTruthy();
  expect(headers1['X-Correlation-Id']).not.toBe(headers2['X-Correlation-Id']);
});
```

(Adapt `jsonResponse`/`fetchMock`/`client` helpers to the file's actual names.)

- [ ] **Step 2: Verify it fails**

```bash
pnpm --filter @marketpulse/api-client test
```

- [ ] **Step 3: Implement** — in `client.ts`'s `request` helper, extend the headers object:

```ts
headers: {
  'Content-Type': 'application/json',
  // Browser-minted correlation: the middleware honours inbound IDs, so ApiError's
  // correlationId now matches a searchable server-side trail end to end.
  'X-Correlation-Id': crypto.randomUUID(),
  ...csrfHeaders(method),
  ...init.headers,
},
```

- [ ] **Step 4: Verify green + full frontend suites**

```bash
pnpm --filter @marketpulse/api-client test && pnpm -r typecheck && pnpm -r test
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(api-client): browser-minted X-Correlation-Id on every request"
```

---

### Task 10: Close the slice — roadmap, full gate, manual rehearsal

**Files:**
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Roadmap** — slice 8 completed-slices row (dense voice: Serilog+OTel both hosts → Aspire, nine instruments, traceparent across the broker, bounded redelivery + ADR-009 amendment, /health/ready, security events, browser correlation); Phase 5 → Partial (8 done; 9 IaC + 10 deployment outstanding); remaining-slices count updated; `grep -n '8 ·\|slice 8\|Observability' docs/ROADMAP.md` for stale references.

- [ ] **Step 2: Full gate**

```bash
dotnet build -c Release   # 0 warnings
dotnet test               # all backend
pnpm -r typecheck && pnpm -r test
cd tests/e2e && pnpm exec playwright test && cd ../..   # /health semantics unchanged, journeys green
```

- [ ] **Step 3: Manual rehearsal (spec's done criteria — record all outcomes + sample evidence in the report)**

```bash
docker compose up -d sqlserver rabbitmq aspire-dashboard   # use the actual rabbit service name from compose
dotnet run --project src/MarketPulse.Api        # terminal 1
dotnet run --project src/MarketPulse.Alerts     # terminal 2
```

Then: (1) register a user, add a ticker, create an alert rule that current prices will trigger; open http://localhost:18888 and find ONE trace spanning API → broker → worker → SQL for the alert firing, with `correlation.id` on it and matching log lines; (2) watch the nine custom metrics move under live traffic; (3) with `RabbitMq__RetryLimit=2` set on the API and SQL container stopped (`docker compose stop sqlserver`), trigger an alert-notification persist failure and watch redelivery warnings, counter movement, and the message land in the DLQ (`api.notifications.dlq` — inspect via the RabbitMQ management UI or `rabbitmqadmin`); restart SQL after. Record: screenshots-worth of description, the trace ID, counter values, DLQ depth.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs: close slice 8 — observability shipped, phase 5 opened"
```

Do **not** merge to `test` — finishing-a-development-branch handles that after the final whole-branch review.

---

## Self-review notes (already applied)

- **Spec coverage:** decisions 1–5 → Tasks 1–5+8; nine instruments (T1) wired in T5/6/7; security events T6; health T8; browser correlation T9; ADR-009 amendment inside T5; compose + Aspire T2; manual rehearsal T10 matches the spec's done criteria including the forced-failure DLQ leg.
- **Spec's error-surface table:** collector-down inertness proven by CI's very existence + T2 Step 5; readiness 503s exercised partially (T8's integration test accepts either status and pins both check names — the broker-down 503 is unit-covered via `RabbitMqHealthCheckTests` and rehearsed manually; recorded as the deliberate fidelity trade).
- **Layering:** `Telemetry` + `OtelOptions` + `EmailMasking` in Application (BCL-only); `MessagingTelemetry` in Infrastructure (needs `OpenTelemetry.Api` + RabbitMQ.Client — both already Infrastructure dependencies); Serilog confined to the two `Program.cs` files; `DependencyRuleTests` re-run in T1.
- **Type consistency:** `TransientRetry.RetryOrDeadLetterAsync(channel, ea, queueName, retryLimit, logger, ct)` identical at definition (T5 impl), unit tests, integration tests, and the consumer call site; counter names in T6/T7 tests match T1's instrument strings exactly.
- **Known adaptation points, named:** package versions (restore-time substitution allowed, recorded), NSubstitute syntax for `IChannel.BasicPublishAsync`, `RabbitMqFixture` member names, auth-handler local identifiers, existing handler-test constructor updates. Each is scoped and instructs "read the file first, keep semantics identical".
