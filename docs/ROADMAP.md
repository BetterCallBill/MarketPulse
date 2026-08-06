# Roadmap

The README's delivery plan describes the finished product. This document describes where the
repository actually is, what remains, and in what order it gets built. When the two disagree,
this one is right.

**Verified against commit `799e1c5` on `feature/slice-7b-price-chart-ui`, 2026-08-06**
(the branch's code head immediately before this docs commit — a commit cannot cite its own
hash before it exists). Every status below was checked against the source tree, not against
documentation.

## How this document is used

- Each remaining slice becomes one spec (`docs/superpowers/specs/`) and one plan
  (`docs/superpowers/plans/`), built and merged to `test` before the next begins.
- Update the status table in the same commit that finishes a slice. A slice is not done
  until its row says so.
- The deferred-claims register at the bottom tracks every place the README currently
  describes something that does not exist. Each entry gets resolved by building the thing
  or by deleting the claim — not by leaving it.

## Status legend

| Mark | Meaning |
|---|---|
| Done | Built, tested, merged to `test` |
| Partial | Some of the phase's deliverables exist; the row says which do not |
| Not started | No code |

---

## Phase status

| Phase | Status | Present | Absent |
|---|---|---|---|
| 1 · Backend foundation | **Done** | Clean Architecture layering (enforced by `DependencyRuleTests`), EF Core + 6 migrations, cookie auth with refresh-token rotation, CSRF, auth rate limiting, watchlist CRUD, `Portfolio` aggregate (holdings, average-cost basis, realised P&L), buy/sell transactions, the idempotency-key store | — |
| 2 · Real-time core | **Done** | SignalR hub + fan-out, `PriceTickChannel`, tick delivery to the dashboard, the real Yahoo Finance market-data feed behind a retry/breaker/timeout resilience pipeline (`MarketData:Source=Yahoo`, [ADR-010](adr/010-market-data-feed.md)), tick persistence (`PersistingTickSink`/`TickBuffer`/`TickPersistenceService`, batched set-based INSERT into `PriceTicks`), chunked 7-day retention sweep, Dapper history reads (OHLC candles, set-based sparklines) behind `GET /api/v1/prices/{ticker}/candles` and `GET /api/v1/prices/sparklines` — `FakeTickService`'s random walk stays the default for tests and offline development, with no fallback to it if the real feed fails; the dashboard chart surface (`Sparkline` in the watchlist, lazy-loaded `CandleChart` behind `/prices/:ticker`) closing the loop 7b | — |
| 3 · Messaging & alerts | **Done** | RabbitMQ, the outbox, the Alerts worker, alert rules, notifications, per-user delivery, and the chaos test proving zero lost alerts across a broker kill/restart | — |
| 4 · Frontend core | **Done** | `packages/ui` token system + primitives, `packages/api-client` (zod-validated, no direct `fetch` anywhere in the app), TanStack Query for server state, auth screens, watchlist table with live price cells, alerts and notifications UI (inline rule control, notifications panel), header navigation (`Watchlist \| Portfolio`), portfolio dashboard (holdings table with render-derived live unrealised P&L, trade form with submission-scoped idempotency keys, transaction history with load-more) | — |
| 5 · Cloud & pipeline | **Not started** | CI runs backend tests, frontend tests, and Playwright E2E against a real database | Terraform, ECS, CloudFront/S3, Lambda snapshot, OpenTelemetry, deployment pipeline |
| 6 · Hardening | **Not started** | Testcontainers integration suite, one E2E journey (authentication) | CSP, threat model, performance pass, RUM, load test, the documentation set |

Two phases untouched, four done.

Phase 4's remainder had no slice of its own: the alerts and notifications UI landed in 4b, and
took the client-state question with it. 4b's unread-notification handling was the trigger the
previous version of this section predicted, and it resolved the other way that prediction
allowed for — unread state turned out to be server state already exposed by the API
(`IsRead`, `POST /api/v1/notifications/{id}/read`), not a genuine client-owned value, so
nothing needed a second store. Zustand was not added merely to satisfy ADR-007; the ADR was
rewritten instead, to describe what the application actually does — see
[ADR-007](adr/007-state-architecture.md).

Phase 4's other remainder — the dashboard surface for the portfolio 5a's backend built — did
get its own slice: 5b, which lands the `/portfolio` route, the live unrealised P&L derivation
(ADR-007's third worked example), and the trade form's idempotency-key discipline, closing the
phase.

---

## Completed slices

| Slice | Date | Delivered |
|---|---|---|
| 1 · Walking skeleton | 2026-07-31 | Clean Architecture skeleton, EF Core + migrations, watchlist CRUD, SignalR fan-out off a fake tick source, CI |
| 2 · Authentication | 2026-08-03 | Cookie sessions, refresh-token rotation, CSRF middleware, rate limiting, cross-user isolation tests, E2E auth journey |
| 3 · Design system | 2026-08-03 | `packages/ui` two-layer design tokens, six primitives, dashboard restyle, contrast ratios asserted in CI |
| 4a · Alerts pipeline — backend | 2026-08-04 | RabbitMQ topology, transactional outbox, the `MarketPulse.Alerts` worker, alert rule CRUD, per-user notification delivery over `NotificationHub`, ADR-009. Proven end to end by integration tests against real SQL Server and real RabbitMQ. No dashboard changes |
| 4b · Alerts UI and chaos test | 2026-08-05 | `features/alerts` (inline rule control on watchlist rows) and `features/notifications` (bell badge, dropdown panel, mark-read-on-open) on `packages/ui`; `useNotificationStream` patching SignalR pushes into the TanStack Query cache; ADR-007 (client state is the server cache — Zustand rejected); the `alerts.spec.ts` Playwright journey with the Alerts worker spawned from global-setup; `ChaosTests.cs`, the chaos test that stops RabbitMQ between a rule triggering and its outbox row dispatching and proves exactly one notification survives the restart. Two of 4a's carried-over review findings fixed along the way |
| 5a · Portfolio backend | 2026-08-05 | `Portfolio` aggregate (implicit per-user creation, `Holding`s, average-cost basis, realised P&L) behind MediatR commands/queries over one store (ADR-004); `Portfolio.Version`, a monotonic counter that keeps the aggregate's `RowVersion` guarding every trade even though a buy/sell only touches a `Holding` row; stored-key idempotency (`IdempotencyFilter`) on `POST /portfolio/transactions` and `POST /alerts`, settling the debt 4a deferred; `PortfolioConcurrencyAnomalyTests`, the oversell lost-update anomaly reproduced with the concurrency token bypassed and prevented with it. Backend-only, on the 4a/4b precedent — no dashboard changes |
| 5b · Portfolio dashboard | 2026-08-05 | `/portfolio` route and header navigation (`Watchlist \| Portfolio`, session-gated); `HoldingsTable` with unrealised P&L derived at render from the `['portfolio']` cache × the price stream (ADR-007's third worked example), em-dash for an unticked holding and for the footer total when any held ticker lacks a price; `TradeForm` with submission-scoped idempotency keys (`crypto.randomUUID()` minted per submission, reused by Retry after a 409, fresh on the next submission), no optimistic updates; `TransactionHistory`'s growing take-window load-more; the `portfolio.spec.ts` Playwright journey (buy, sell, P&L, history, reload); Vitest coverage of the key lifecycle 5a's server-side tests couldn't see. Closes phase 4 |
| 6 · Real market-data ingestion | 2026-08-05 | `YahooPriceFeedService` (`BackgroundService`, per-symbol concurrent polls of the keyless `v8/finance/chart/{symbol}` endpoint every 60s (25 req/min against the 25-symbol seed set), ASX↔`.AX` symbol mapping, observation-time timestamps) writing into the same `PriceTickChannel` the fake fills; `MarketDataResilience`'s declared retry (3×, exponential backoff with jitter) → circuit breaker (`FailureRatio` 0.9, `MinimumThroughput` 6, 90s sampling, 2min break) → per-attempt timeout pipeline on the typed `YahooQuoteClient`, proven against a `FakeTimeProvider`; `MarketData:Source=Fake\|Yahoo` composition switch (`Fake` default, bad value fails startup via `ValidateOnStart`); ADR-010 (feed selection, the tolerated-failure-modes table, the no-fallback product decision). Per-symbol bad quotes are skipped with one warning, siblings unaffected; a failed poll writes nothing and the next self-heals; no fallback to the fake. Task 6's manual rehearsal against the live upstream ran 2026-08-05/06: real prices confirmed (independent curl and the service's own successful polls agreed, e.g. IVV.AX 73.33 vs the 62.10 seed) and the dead-endpoint failure path confirmed (`Market-data circuit opened for 00:02:00 after sustained failures.`, health stayed 200 throughout) — see task-6-report.md. The live alert-fire leg was attempted but not captured: this sandbox's egress IP hit sustained Yahoo rate-limiting (429) partway through the session, which the breaker itself correctly suppressed, leaving no further real ticks to trigger a rule before the session's time budget ran out |
| 7a · Price history backend | 2026-08-06 | Every real/fake tick now persists: `PersistingTickSink` fans into a bounded `TickBuffer`, `TickPersistenceService` flushes it on a timer as one set-based, chunked INSERT per batch into `PriceTicks` (clustered natural key `(Ticker, TimestampUtc)`, `IGNORE_DUP_KEY` absorbing duplicate observations rather than erroring), scoped per flush so no long-lived `DbContext` sits across the buffer's lifetime; `TickRetentionService` sweeps rows older than `History:RetentionDays` (default 7) in bounded chunks so a single delete never locks the table for the whole window; a Dapper read path (`IPriceHistoryReader`, bypassing EF Core for read-side aggregation) serving OHLC candle queries and a set-based sparklines query behind `GET /api/v1/prices/{ticker}/candles` and `GET /api/v1/prices/sparklines`, both with a slugged error contract (404 unknown-ticker, 400 invalid-interval/invalid-range/range-too-large) and a cross-user isolation test. Task 10 shipped a deliberate per-ticker N+1 in the sparklines handler — correct, fully covered by response-shape tests, and structurally invisible to them; Task 11 pinned it with a connection-counting test (`Expected: 1, Actual: 3`), replaced it with one set-based query, and measured both versions rather than asserting the fix mattered: [ADR-006](adr/006-n-plus-one-postmortem.md) and `docs/sql/` record the honest result — server-side query cost is a wash (112 vs. 114 logical reads, `PriceTicks` scanned 20 times either way), the real and structural win is round-trip count (20 connections collapsed to 1), measured end-to-end at 32.9ms → 22.5ms average (~32% faster) for a 20-ticker watchlist against an identical 404,625-row table. Backend-only, on the 5a/6 precedent — no dashboard changes; the chart UI is 7b |
| 7b · Price history charts | 2026-08-06 | Zod candle/sparkline contracts and `getCandles`/`getSparklines` client methods over 7a's endpoints; `Sparkline` (`packages/ui`, hand-rolled SVG, no charting dependency for the watchlist's decoration-only trend); a Trend column on the watchlist wired to it, each ticker linking to `/prices/:ticker`; `CandleChart` (`lightweight-charts` 5.2.0, a StrictMode-safe create/dispose lifecycle so double-invoked effects in dev don't leak or double-render a chart); `PriceHistoryScreen` behind a `React.lazy` route with an interval switcher (1m/5m/1h/1d) and `useCandles`, an interval-scaled SWR hook, plus honest loading/empty/error/unknown-ticker states — the last with a working link back to the watchlist. Chunk-split verified: the chart library and screen isolate to a 167.9KB `PriceHistoryScreen` chunk, loaded only on navigation to `/prices/:ticker`; the 362.2KB main entry chunk stays clean of `lightweight-charts`. The `chart.spec.ts` Playwright journey covers the lazy route, a real rendered canvas, and interval switching; one deviation from spec — the sparkline assertion relaxed from polyline-shape to Trend-column presence, because the fresh seed data doesn't yet have two minute-buckets to draw a line between — recorded in task-7 and task-8 reports, not silently dropped. 1d is visibly thin against the 7-day retention window; it fills in as the window ages, not a defect. No backend changes — 7a's read path and error contract were the full surface this slice needed; `from`/`to` are computed per query and excluded from the query key — a recorded deviation from the spec's interval-rounding sketch, same cache behaviour, simpler key. Closes phase 2 |

#### 4a review findings: resolved and outstanding

4a's review deferred five findings, none blocking, recorded in this document because slice
ledgers live in gitignored `.superpowers/` and do not survive the branch. Two landed as fixes
in 4b, while the messaging code they touch was still fresh:

- **Fixed.** `RabbitMqEventPublisher.DisposeAsync` now takes `_gate` before disposing it —
  publisher disposal is serialised against an in-flight `PublishAsync`, copying the shape
  `RabbitMqConnection` already used, and the code comment's claim now matches what it delivers.
- **Fixed.** `RabbitMqConsumerService` now keeps a floor of one delay after a
  shutdown-triggered resubscribe, so a channel that died immediately after a successful
  subscribe cannot re-loop with no backoff.

Of the remaining three, one has a new home and two stay recorded here, both non-blocking:

- **The transient requeue loop is still unbounded.** Its home is the observability slice (8):
  distinguishing a slow-burning transient fault from a permanent one needs a redelivery
  counter or a delayed retry queue, and somewhere to see it happening — named in the 4a spec,
  ADR-009's consequences, and again in slice 8's entry below.
- A channel can still be disposed under an in-flight `HandleAsync`, whose subsequent ack then
  throws. At-least-once is preserved (the delivery was never acked and the channel is dead
  anyway); the effect is log noise. No slice owns this; it remains recorded here.
- `BrokerOutageTests` still leaves a durable randomly-named queue and binding per run. The
  broker container is per-run, so it self-cleans; hygiene only, no slice owns this either.

4a's spec also left its manual done-criteria unrehearsed — starting both processes by hand,
watching a real alert fire, and stopping/restarting the broker to see the outbox flush. A
pre-existing SQL Server container blocked `docker compose up` at the time. `ChaosTests.cs` is
that rehearsal automated: a real API host and real worker services (`PriceConsumer` hosted;
`OutboxDispatcher` driven by hand through its public `DispatchPendingAsync` seam, deliberately
not hosted, so the broker can be stopped deterministically inside the Triggered→dispatched
window) against Testcontainers SQL Server and RabbitMQ, the broker container stopped and
restarted mid-flow, asserting the outbox row survives and dispatches exactly once after
restart.

---

## Remaining slices

Six slices remain. Sizing assumes the ~8-task shape of slices 1–3; slices marked **may
split** are the ones most likely to exceed it.

### 8 · Observability · phase 5
OpenTelemetry traces and metrics, Serilog structured logging built out from the existing
`CorrelationIdMiddleware`, health checks, dashboards. Wanted before deployment, so that the
first production incident is diagnosable. Also where the consumer's unbounded transient
requeue loop (carried over from 4a, still open after 4b) gets a redelivery counter or a
delayed retry queue — and somewhere to see it happening, which is the point of this slice.

*Depends on:* nothing outstanding.

### 9 · Infrastructure as code · phase 5 · may split
Terraform for VPC, RDS SQL Server, ECS Fargate, ALB, ECR, CloudFront + S3. `docs/cost-model.md`
with the free-tier strategy. Success criterion: reproducible from scratch with `terraform apply`.

*Depends on:* 8, so that what gets deployed is already instrumented.

### 10 · Deployment pipeline · phase 5
ECS blue-green deployment, GitHub Actions deploy workflow, Lambda end-of-day price snapshot.

*Depends on:* 9.

### 11 · Security hardening · phase 6
CSP with no `unsafe-inline` in production, security headers, `docs/THREAT-MODEL.md`,
dependency scanning, secrets handling review.

*Depends on:* 10 for a production surface to harden, though the threat model can be written
earlier.

### 12 · Performance pass · phase 6
BenchmarkDotNet suite, documented load test to the p95 < 200ms criterion, index analysis,
`docs/PERFORMANCE.md` with before/after measurements, RUM, Lighthouse ≥ 95.

*Depends on:* 10 for a realistic environment to measure.

### 13 · Documentation backfill · phase 6
The missing ADRs (004 CQRS scope, 005 captive-dependency postmortem — 006 N+1 postmortem and
007 state architecture were written early, in 7a and 4b respectively), `docs/api-style-guide.md`,
and the six engineering write-ups.

*Depends on:* the slices whose decisions they record. Some ADRs should be written earlier,
alongside the work — see the register below.

---

## Open ordering questions

One decision this document does not make (a second — portfolio before or after alerts — is
answered: alerts (slices 4a/4b) landed first, on the reasoning that it was the stronger
engineering story):

1. **Does the micro-frontend get built?** `apps/alerts-mfe` (Module Federation) is documented
   but unscheduled. It is a substantial architectural commitment that no phase currently
   covers. Either it earns a slice or it comes out of the README.

---

## Deferred-claims register

The README currently describes these as existing. They do not. Each needs building or deleting.

| Claim | Location | Resolution |
|---|---|---|
| `docs/adr/004-cqrs-scope.md` | README architecture | **Resolved in 5a** — see [ADR-004](adr/004-cqrs-scope.md): MediatR commands/queries over one store, the fuller read-store and event-sourced alternatives named and rejected. The README's claim is corrected to match |
| `docs/adr/005-captive-dependency-postmortem.md` | README | Slice 13 — requires the engineered incident to have happened |
| `docs/adr/006-n-plus-one-postmortem.md` | README architecture | **Resolved in 7a** — see [ADR-006](adr/006-n-plus-one-postmortem.md): sparklines' deliberate per-ticker N+1, pinned with a connection-counting test, replaced with one set-based query, measured before and after. The README's claim is corrected to match |
| `docs/adr/007-state-architecture.md` | README architecture | **Resolved in 4b** — see [ADR-007](adr/007-state-architecture.md): client state is the server cache, Zustand rejected. The README's claim is corrected to match |
| `docs/PERFORMANCE.md` | README docs index | Slice 12 |
| `docs/THREAT-MODEL.md` | README docs index | Slice 11 |
| `docs/api-style-guide.md` | README docs index | Slice 13 |
| `docs/cost-model.md` | README docs index | Slice 9 |
| `docs/benchmarks/` | README docs index | Slice 12 |
| `apps/alerts-mfe` | README frontend structure | **Unscheduled** — see open question 2 |
| `packages/emitter` | README frontend structure | **Unscheduled** — no phase covers it; likely delete the claim |
| Storybook | README frontend structure | Already annotated "not yet" in the README; no phase covers it |

Seven of these are dead links in the README today. Until each is resolved, the README should
mark them as planned rather than present them as description.

(`docs/sql/`, present since 7a, left the table entirely rather than gaining a "Resolved in
7a" row: an ADR is a single named decision worth a permanent cross-reference back to the slice
that wrote it, but `docs/sql/` is just a directory — once it exists, there is nothing left to
resolve or point back to.)
