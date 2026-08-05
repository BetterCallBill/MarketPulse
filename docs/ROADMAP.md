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
| 3 · Messaging & alerts | **Done** | RabbitMQ, the outbox, the Alerts worker, alert rules, notifications, per-user delivery, and the chaos test proving zero lost alerts across a broker kill/restart | — |
| 4 · Frontend core | **Partial** | `packages/ui` token system + primitives, `packages/api-client` (zod-validated, no direct `fetch` anywhere in the app), TanStack Query for server state, auth screens, watchlist table with live price cells, alerts and notifications UI (inline rule control, notifications panel) | ADR-007 written: server cache only |
| 5 · Cloud & pipeline | **Not started** | CI runs backend tests, frontend tests, and Playwright E2E against a real database | Terraform, ECS, CloudFront/S3, Lambda snapshot, OpenTelemetry, deployment pipeline |
| 6 · Hardening | **Not started** | Testcontainers integration suite, one E2E journey (authentication) | CSP, threat model, performance pass, RUM, load test, the documentation set |

Two phases untouched, three partly built, one done.

Phase 4's remainder had no slice of its own: the alerts and notifications UI landed in 4b, and
took the client-state question with it. 4b's unread-notification handling was the trigger the
previous version of this section predicted, and it resolved the other way that prediction
allowed for — unread state turned out to be server state already exposed by the API
(`IsRead`, `POST /api/v1/notifications/{id}/read`), not a genuine client-owned value, so
nothing needed a second store. Zustand was not added merely to satisfy ADR-007; the ADR was
rewritten instead, to describe what the application actually does — see
[ADR-007](adr/007-state-architecture.md).

---

## Completed slices

| Slice | Date | Delivered |
|---|---|---|
| 1 · Walking skeleton | 2026-07-31 | Clean Architecture skeleton, EF Core + migrations, watchlist CRUD, SignalR fan-out off a fake tick source, CI |
| 2 · Authentication | 2026-08-03 | Cookie sessions, refresh-token rotation, CSRF middleware, rate limiting, cross-user isolation tests, E2E auth journey |
| 3 · Design system | 2026-08-03 | `packages/ui` two-layer design tokens, six primitives, dashboard restyle, contrast ratios asserted in CI |
| 4a · Alerts pipeline — backend | 2026-08-04 | RabbitMQ topology, transactional outbox, the `MarketPulse.Alerts` worker, alert rule CRUD, per-user notification delivery over `NotificationHub`, ADR-009. Proven end to end by integration tests against real SQL Server and real RabbitMQ. No dashboard changes |
| 4b · Alerts UI and chaos test | 2026-08-05 | `features/alerts` (inline rule control on watchlist rows) and `features/notifications` (bell badge, dropdown panel, mark-read-on-open) on `packages/ui`; `useNotificationStream` patching SignalR pushes into the TanStack Query cache; ADR-007 (client state is the server cache — Zustand rejected); the `alerts.spec.ts` Playwright journey with the Alerts worker spawned from global-setup; `ChaosTests.cs`, the chaos test that stops RabbitMQ between a rule triggering and its outbox row dispatching and proves exactly one notification survives the restart. Two of 4a's carried-over review findings fixed along the way |

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

Nine slices remain. Sizing assumes the ~8-task shape of slices 1–3; slices marked **may split**
are the ones most likely to exceed it.

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
The missing ADRs (004 CQRS scope, 005 captive-dependency postmortem, 006 N+1 postmortem —
007 state architecture was written early, in 4b), `docs/api-style-guide.md`, and the six
engineering write-ups.

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
| `docs/adr/004-cqrs-scope.md` | README architecture | Slice 13, or write when CQRS scope is next revisited |
| `docs/adr/005-captive-dependency-postmortem.md` | README | Slice 13 — requires the engineered incident to have happened |
| `docs/adr/006-n-plus-one-postmortem.md` | README | Slice 7, where the N+1 is deliberately introduced and fixed |
| `docs/adr/007-state-architecture.md` | README architecture | **Resolved in 4b** — see [ADR-007](adr/007-state-architecture.md): client state is the server cache, Zustand rejected. The README's claim is corrected to match |
| `docs/PERFORMANCE.md` | README docs index | Slice 12 |
| `docs/THREAT-MODEL.md` | README docs index | Slice 11 |
| `docs/api-style-guide.md` | README docs index | Slice 13 |
| `docs/cost-model.md` | README docs index | Slice 9 |
| `docs/sql/` | README docs index | Slice 7 |
| `docs/benchmarks/` | README docs index | Slice 12 |
| `apps/alerts-mfe` | README frontend structure | **Unscheduled** — see open question 2 |
| `packages/emitter` | README frontend structure | **Unscheduled** — no phase covers it; likely delete the claim |
| Storybook | README frontend structure | Already annotated "not yet" in the README; no phase covers it |

Nine of these are dead links in the README today. Until each is resolved, the README should
mark them as planned rather than present them as description.
