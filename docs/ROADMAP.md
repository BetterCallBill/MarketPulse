# Roadmap

The README's delivery plan describes the finished product. This document describes where the
repository actually is, what remains, and in what order it gets built. When the two disagree,
this one is right.

**Verified against commit `e229fc0` on `test`, 2026-08-04.** Every status below was checked
against the source tree, not against documentation.

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
| 1 · Backend foundation | **Partial** | Clean Architecture layering (enforced by `DependencyRuleTests`), EF Core + 3 migrations, cookie auth with refresh-token rotation, CSRF, auth rate limiting, watchlist CRUD | Portfolio aggregate, holdings, transactions |
| 2 · Real-time core | **Partial** | SignalR hub + fan-out, `PriceTickChannel`, tick delivery to the dashboard | Real market-data feed (ticks come from `FakeTickService`, a random walk), tick persistence, Dapper history queries |
| 3 · Messaging & alerts | **Partial** | RabbitMQ, the outbox, the Alerts worker, alert rules, notifications, per-user delivery | Alerts UI, the chaos test |
| 4 · Frontend core | **Partial** | `packages/ui` token system + primitives, `packages/api-client` (zod-validated, no direct `fetch` anywhere in the app), TanStack Query for server state, auth screens, watchlist table with live price cells | Client-state layer (no Zustand — ADR-007's "two-layer" claim is currently half-true), alerts and notifications UI |
| 5 · Cloud & pipeline | **Not started** | CI runs backend tests, frontend tests, and Playwright E2E against a real database | Terraform, ECS, CloudFront/S3, Lambda snapshot, OpenTelemetry, deployment pipeline |
| 6 · Hardening | **Not started** | Testcontainers integration suite, one E2E journey (authentication) | CSP, threat model, performance pass, RUM, load test, the documentation set |

Two phases untouched, four partly built.

Phase 4's remainder has no slice of its own: the alerts and notifications UI lands in 4b, and
the client-state layer lands in whichever slice first needs state that is genuinely not server
cache. 4b's unread-notification handling is the likely trigger. If nothing ever needs it,
Zustand should not be added merely to satisfy ADR-007 — the ADR should be rewritten to
describe what the application actually does.

---

## Completed slices

| Slice | Date | Delivered |
|---|---|---|
| 1 · Walking skeleton | 2026-07-31 | Clean Architecture skeleton, EF Core + migrations, watchlist CRUD, SignalR fan-out off a fake tick source, CI |
| 2 · Authentication | 2026-08-03 | Cookie sessions, refresh-token rotation, CSRF middleware, rate limiting, cross-user isolation tests, E2E auth journey |
| 3 · Design system | 2026-08-03 | `packages/ui` two-layer design tokens, six primitives, dashboard restyle, contrast ratios asserted in CI |
| 4a · Alerts pipeline — backend | 2026-08-04 | RabbitMQ topology, transactional outbox, the `MarketPulse.Alerts` worker, alert rule CRUD, per-user notification delivery over `NotificationHub`, ADR-009. Proven end to end by integration tests against real SQL Server and real RabbitMQ. No dashboard changes |

---

## Remaining slices

Ten slices remain. Sizing assumes the ~8-task shape of slices 1–3; slices marked **may split**
are the ones most likely to exceed it.

### 4b · Alerts UI and chaos test · phase 3
Alert management and notifications panel built on `packages/ui`, unread state, Playwright
journey (set alert → price crosses → notification appears), and the chaos test that kills
RabbitMQ mid-flow and proves zero lost alerts.

*Depends on:* 4a. **The README's headline claim — alerts that survive an outage — is not
substantiated until this slice lands.**

#### Carried over from 4a

4a's reviews deferred five findings, none blocking, recorded here because slice ledgers live
in gitignored `.superpowers/` and do not survive the branch. The first two are the ones worth
fixing while the messaging code is still fresh:

- **`RabbitMqEventPublisher.DisposeAsync` does not take `_gate` before disposing it.** The
  `_disposed` flag narrows the race with a concurrent `PublishAsync` but does not close it, so
  a caller past the flag check can still meet a disposed semaphore. `RabbitMqConnection` does
  this correctly — copy its shape. The code comment currently claims a guarantee slightly
  stronger than what it delivers.
- **`RabbitMqConsumerService` resets its retry delay to zero on a successful subscribe**, so a
  channel that died immediately after every successful subscribe would re-loop with no backoff.
  No message-driven path can close a channel deterministically today, so this is theoretical —
  a floor of one delay after a shutdown-triggered resubscribe removes the class.
- A channel can be disposed under an in-flight `HandleAsync`, whose subsequent ack then throws.
  At-least-once is preserved (the delivery was never acked and the channel is dead anyway); the
  effect is log noise.
- **The transient requeue loop is still unbounded.** Already named in the 4a spec and ADR-009 as
  observability-slice work: distinguishing a slow-burning transient fault from a permanent one
  needs a redelivery counter or a delayed retry queue, and somewhere to see it happening.
- `BrokerOutageTests` leaves a durable randomly-named queue and binding per run. The broker
  container is per-run, so it self-cleans; hygiene only.

**Also outstanding from 4a:** its spec's manual done-criteria were never rehearsed — starting
both processes by hand, watching a real alert fire, and stopping/restarting the broker to see
the outbox flush. A pre-existing SQL Server container blocked `docker compose up` at the time.
The automated suite covers the behaviour, including a real broker-severance test, but the
hand-run rehearsal is exactly what 4b's chaos test automates, so it lands naturally here.

### 5 · Portfolio and transactions · phase 1 · may split
`Portfolio` aggregate, holdings, buy/sell transactions, cost basis, realised and unrealised
P&L, and the dashboard surface for them. Closes the largest gap between the README's product
description and the running application. Likely splits backend/frontend.

*Depends on:* nothing outstanding. Could be built before slice 4 if product completeness
matters more than the messaging story.

### 6 · Real market-data ingestion · phase 2
Replace `FakeTickService` with a real feed behind Polly retry and circuit breaker, keeping
the fake behind configuration for tests and offline development. ADR on feed selection and
the failure modes chosen to tolerate.

*Depends on:* nothing outstanding, but is more valuable after 4a — a real feed makes alerts
real rather than a demonstration against a random walk.

### 7 · Price history and charts · phase 2 · may split
Tick persistence, Dapper history queries, candle aggregation, dashboard chart. Carries the
deliberate N+1 postmortem (ADR-006) and the SQL execution-plan analysis in `docs/sql/`.

*Depends on:* 6 for data worth charting.

### 8 · Observability · phase 5
OpenTelemetry traces and metrics, Serilog structured logging built out from the existing
`CorrelationIdMiddleware`, health checks, dashboards. Wanted before deployment, so that the
first production incident is diagnosable.

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
The missing ADRs (004 CQRS scope, 005 captive-dependency postmortem, 006 N+1 postmortem,
007 state architecture), `docs/api-style-guide.md`, and the six engineering write-ups.

*Depends on:* the slices whose decisions they record. Some ADRs should be written earlier,
alongside the work — see the register below.

---

## Open ordering questions

Two decisions this document does not make:

1. **Portfolio (slice 5) before or after alerts (slice 4)?** Alerts is the stronger
   engineering story; portfolio is the larger product gap. Current order puts alerts first.
2. **Does the micro-frontend get built?** `apps/alerts-mfe` (Module Federation) is documented
   but unscheduled. It is a substantial architectural commitment that no phase currently
   covers. Either it earns a slice or it comes out of the README.

---

## Deferred-claims register

The README currently describes these as existing. They do not. Each needs building or deleting.

| Claim | Location | Resolution |
|---|---|---|
| `docs/adr/004-cqrs-scope.md` | README architecture | Slice 13, or write when CQRS scope is next revisited |
| `docs/adr/005-captive-dependency-postmortem.md` | README | Slice 13 — requires the engineered incident to have happened |
| `docs/adr/006-n-plus-one-postmortem.md` | README | Slice 7, where the N+1 is deliberately introduced and fixed |
| `docs/adr/007-state-architecture.md` | README architecture | Write when the client-state layer lands, or rewrite to describe server cache alone if it never does. The "TanStack Query + Zustand" claim is currently half-true: Zustand is not a dependency |
| `docs/PERFORMANCE.md` | README docs index | Slice 12 |
| `docs/THREAT-MODEL.md` | README docs index | Slice 11 |
| `docs/api-style-guide.md` | README docs index | Slice 13 |
| `docs/cost-model.md` | README docs index | Slice 9 |
| `docs/sql/` | README docs index | Slice 7 |
| `docs/benchmarks/` | README docs index | Slice 12 |
| `apps/alerts-mfe` | README frontend structure | **Unscheduled** — see open question 2 |
| `packages/emitter` | README frontend structure | **Unscheduled** — no phase covers it; likely delete the claim |
| Storybook | README frontend structure | Already annotated "not yet" in the README; no phase covers it |

Ten of these are dead links in the README today. Until each is resolved, the README should
mark them as planned rather than present them as description.
