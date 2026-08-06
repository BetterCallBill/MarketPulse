# Slice 8 — Observability

**Date:** 2026-08-06
**Status:** Approved
**Slice:** 8 of the roadmap (phase 5 opener). No outstanding dependencies; wanted before
deployment so the first production incident is diagnosable.

## Why this slice exists

The correlation ID is threaded through every request and message but lands nowhere
searchable. Failed logins, lockouts and refresh-token reuse detections — exactly the events
a real system alerts on — emit nothing. `/health` is a hardcoded `{status:"ok"}` with no
dependency checks. And ADR-009 records a real defect on the clock: `AlertTriggeredConsumer`
nacks transient database failures with `requeue: true`, so a message can loop between
consumer and queue at broker speed, unbounded, while SQL Server is down — "a redelivery
counter, or a delayed retry queue" was named as this slice's work. This slice makes the
system observable and closes that loop.

### Decisions taken, with rejected alternatives

1. **OTLP to the .NET Aspire dashboard as the local telemetry sink.** One container
   (`mcr.microsoft.com/dotnet/aspire-dashboard`) receives traces, metrics and structured
   logs from both hosts, searchable by correlation ID — "somewhere to see it happening"
   with zero provisioning. *Rejected:* Prometheus + Grafana (+ Tempo/Loki) — the
   production-classic stack and a stronger portfolio artifact, but 3–4 containers of
   provisioning that slice 9/10 would rework anyway (the README names Datadog/CloudWatch
   for cloud); Seq + Jaeger (two UIs, no metrics story). Authored dashboards-as-code are
   deliberately deferred to the cloud slices, and saying so is part of the design.
2. **Redelivery counter → dead-letter after a cap, not a delayed retry queue.** The
   transient path republishes the message to the same queue with an incremented
   `x-retry-count` header and acks the original; at the cap (`RabbitMqOptions.RetryLimit`,
   default 5) it dead-letters instead. No new topology; the loop terminates in ≤5 broker
   round-trips; every step emits a metric and a structured warning. *Rejected:* delayed
   retry queue (DLX + TTL parking queue) — real backoff and the better production shape,
   but new topology, new failure modes, roughly twice the slice weight, and it still wants
   a cap. ADR-009 listed both; the counter is its first-named option. The trade-off is
   recorded: retries stay hot until the cap.
3. **Serilog stays behind `ILogger<T>`.** Serilog is bootstrap-only (each host's
   `Program.cs`); no application code takes a Serilog dependency. Two sinks: compact-JSON
   console and OTLP. *Rejected:* Serilog's static `Log` API in handlers (couples every
   layer to a logging library the architecture tests would have to police); OTel's own
   `ILogger` provider alone (loses Serilog, which the roadmap and README both name —
   enrichment, request logging, and the EF-noise filtering come with it).
4. **`/health` stays dependency-free; `/health/ready` gets the real checks.** The existing
   endpoint is the liveness probe — Playwright's webServer poll and any future load
   balancer depend on it answering 200 whenever the process is alive, downstream outages
   included. Readiness (SQL Server via the EF context, RabbitMQ via the connection) lives
   at `/health/ready`. *Rejected:* upgrading `/health` in place (an e2e boot that hangs
   because the broker container was slow would be a self-inflicted flake).
5. **Trace context crosses the broker by hand.** The publish path stamps W3C `traceparent`
   (via the default `TextMapPropagator`) into RabbitMQ message headers; consumers restore
   it before starting their span, so one trace covers API → broker → worker → SQL. The
   existing `CorrelationId` basic property stays — it is the human-facing key and the two
   are complementary, not redundant.

## Scope boundary

### In scope

- Serilog bootstrap in `MarketPulse.Api` and `MarketPulse.Alerts` (console JSON + OTLP
  sinks, request logging, correlation enrichment via `CorrelationIdMiddleware`).
- OpenTelemetry tracing (ASP.NET Core, HttpClient, SqlClient, custom messaging
  `ActivitySource`) and metrics (runtime, ASP.NET Core, custom `Meter`) in both hosts,
  OTLP exporter, `aspire-dashboard` service in docker-compose.
- Custom metrics (single `Meter`, nine instruments): ticks persisted, tick-buffer drops,
  alerts evaluated, alerts triggered, notification redeliveries, notification
  dead-letters, auth login failures, account lockouts, refresh-reuse detections.
- The redelivery bound in `AlertTriggeredConsumer` + `RabbitMqOptions.RetryLimit`.
- Structured security events in the auth handlers (failed login, lockout, reuse
  detection) — user ID and masked email only, never credentials.
- `/health/ready` with SQL + RabbitMQ checks; `/health` documented as liveness.
- Frontend: per-request `X-Correlation-Id` minted in the api-client `request` helper.
- ADR-009 amendment: the "requeue loop has no bound" consequence paragraph updated to
  record the bound.

### Out of scope

- Datadog/CloudWatch exporters, Grafana dashboards-as-code, alerting rules, retention
  policies (slices 9/10); sampling tuning (always-on locally); RUM/Core Web Vitals
  (slice 12); the delayed-retry-queue topology (decision 2's rejected alternative); any
  change to the outbox dispatcher's own retry behaviour (it already has bounded semantics
  through its sweep cycle).

## Architecture

### Logging

Each host's `Program.cs` wraps the generic host with Serilog: `UseSerilog` /
`AddSerilog`, minimum level from configuration, compact JSON console sink, OTLP sink
pointed at `Otel:OtlpEndpoint` (default `http://localhost:4317`). The API adds
`UseSerilogRequestLogging()` immediately after `CorrelationIdMiddleware` so the one-line
request event carries the correlation ID. `CorrelationIdMiddleware` grows two lines: push
the ID into the Serilog `LogContext` and tag it onto `Activity.Current`. The Alerts
worker's existing `BeginScope` correlation pattern is untouched — Serilog surfaces scopes
as properties.

When the Aspire dashboard is not running, the OTLP exporters retry quietly in the
background and drop batches; the hosts, tests and CI are unaffected. That behaviour is
verified once (app boots and serves with no collector) and then trusted.

### Tracing and metrics

`AddOpenTelemetry()` in both hosts:

- **Tracing:** `AddAspNetCoreInstrumentation` (API), `AddHttpClientInstrumentation`
  (Yahoo polls), `AddSqlClientInstrumentation`, plus a custom `ActivitySource`
  (`MarketPulse.Messaging`) — `RabbitMqEventPublisher`/`RabbitMqTickSink` start a
  producer span and inject `traceparent` into message headers;
  `RabbitMqConsumerService.HandleAsync` callers extract it and start a consumer span with
  the remote parent. Health-check and OPTIONS requests are filtered out of tracing.
- **Metrics:** runtime + ASP.NET Core instruments, plus one `Meter` named `MarketPulse`
  owned by a small `Telemetry` class (Infrastructure) exposing the nine counters above as
  static instruments. Producers call them where the events happen: the persistence
  service (ticks persisted), `TickBuffer`'s drop callback, `AlertEvaluator`,
  `AlertTriggeredConsumer` (redeliveries, dead-letters), and the auth handlers.
- **Exporter:** OTLP for both signals, same endpoint configuration as logging.

### The redelivery bound

In `AlertTriggeredConsumer`'s transient `DbUpdateException` catch:

1. Read `x-retry-count` from the incoming message headers (absent → 0).
2. If `count < RetryLimit`: republish the same body to the notifications queue with
   `x-retry-count = count + 1` (correlation ID and other properties preserved), then ack
   the original. Warning log + `notification redeliveries` counter, tagged with the
   attempt number.
3. Else: `BasicNack(requeue: false)` — the existing dead-letter route — with an error log
   + `notification dead-letters` counter.

`RetryLimit` lives on `RabbitMqOptions` (`[Range(1, 100)]`, default 5) so integration
tests can drive it low. The permanent-fault and duplicate paths are untouched. The
consequence — a message that survives five transient failures is parked in the DLQ rather
than retried forever — is exactly the trade ADR-009 asked this slice to make, and the
amendment records it.

### Health

`AddHealthChecks()` with two registrations: `DbContext` connectivity (SQL Server) and a
RabbitMQ check reusing `RabbitMqConnection`. `/health` keeps its current anonymous
hardcoded-200 behaviour with a comment naming it the liveness probe; `/health/ready` maps
the checker with the standard JSON response writer, also anonymous (readiness probes
don't authenticate).

### Frontend correlation

`packages/api-client`'s `request` helper adds `'X-Correlation-Id': crypto.randomUUID()`
to every request's headers. The middleware already echoes inbound IDs, so browser-visible
failures (`ApiError.correlationId`) now match a searchable server-side trail end to end.
One client test pins the header's presence and per-request uniqueness.

## Error surface

| Case | Behaviour |
|---|---|
| Aspire dashboard down | Exporters retry/drop quietly; hosts unaffected |
| SQL down (readiness) | `/health/ready` 503; `/health` still 200 |
| Broker down (readiness) | `/health/ready` 503; `/health` still 200 |
| Transient DB fault on consume, attempts < cap | Republish with incremented counter, warning + metric |
| Transient DB fault on consume, attempts = cap | Dead-letter, error + metric |
| Republish itself fails (broker fault mid-recovery) | The original message is not acked — broker redelivery takes over; logged |

## Testing

- **Unit:** retry-decision logic (header absent/below/at cap) — extracted into a small
  pure helper on the consumer so it tests without a broker; auth handlers emit the
  security events and increment the counters (`FakeLogger` + `MeterListener`); masked
  email never contains the full address.
- **Integration (existing Testcontainers infra):** a message published with
  `x-retry-count` at the cap lands in the DLQ; one below the cap is redelivered with the
  header incremented (drive `RetryLimit` low via configuration); `/health/ready` returns
  200 with dependencies up and degrades when the broker connection is unavailable (reuse
  the outage harness from `BrokerOutageTests` if practical, else assert the 200 path and
  unit-test the check wiring); consumer-side log scope carries the correlation ID (already
  covered — extended to assert the restored trace context via an `ActivityListener`).
- **Client:** the `X-Correlation-Id` header is present and unique per request.

### Deliberately not tested

- The Aspire dashboard UI (Microsoft's product; the OTLP export path is exercised by
  booting against it once, manually, in the closing task).
- Exporter batching/retry internals — configuration is trusted; only "no collector, no
  crash" is asserted by the suite's very existence (no collector runs in CI).
- Load behaviour of always-on tracing — sampling policy is slice 12's territory.

## Done criteria

- Full gate green (backend suites, frontend suites, build 0 warnings) with no collector
  running — proving telemetry is optional at runtime.
- Manual rehearsal against `docker compose up`: Aspire dashboard shows (1) one trace
  spanning API → RabbitMQ → worker → SQL for an alert firing, with matching correlation
  ID on its logs; (2) the custom metrics moving under live traffic; (3) a forced
  transient failure (stop SQL container briefly) producing visible redelivery warnings,
  counter movement, and — with `RetryLimit` lowered — a dead-lettered message. Outcomes
  recorded in the task report.
- `/health` unchanged for existing consumers (Playwright suite green); `/health/ready`
  reflects dependency state.
- ADR-009 amended; ROADMAP slice-8 row updated in the closing commit.

## Interview category coverage

This is the slice that finally pays category 12's biggest stated debt ("no observability"
is the appendix's first weakness): OTel traces/metrics/logs, structured logging, health
probes, and dashboards-in-dev. It also advances category 11 (bounded redelivery, DLQ
semantics, trace propagation across a broker — the first real distributed-tracing story),
category 13 (security events that a real system alerts on now exist), and category 1 (the
metrics instruments live on the hot paths and are chosen to be cheap).

## What comes next

Slice 9 (infrastructure as code) deploys what is now instrumented; its cloud sinks
(Datadog/CloudWatch per the README) replace the Aspire endpoint by configuration, not by
code change — that is what the OTLP indirection buys.
