# Slice 1 — Walking Skeleton: "Watchlist walks, prices tick"

**Date:** 2026-07-31
**Status:** Approved (design), pending implementation plan
**Parent spec:** [MarketPulse-Pro-README.md](../../MarketPulse-Pro-README.md)
**Category reference:** [intervew-aspects.md](../../intervew-aspects.md)

---

## Why this slice exists

The parent README describes a 12-week program spanning six independent subsystems: a
modular monolith, an extracted microservice, a messaging layer, a React monorepo with a
micro-frontend, full AWS IaC, and a hardening pass. That is not one spec. Attempting it as
one is how greenfield projects stall around week three, with four half-built layers and
nothing that runs.

This document specs the **first sub-project only**. Each subsequent slice gets its own
spec → plan → implementation cycle.

The slice is cut as a **walking skeleton**: one thin feature wired through every layer,
end to end, rather than one layer built completely. The point is to hit every seam in
week one — including the riskiest one, real-time price delivery, which the parent delivery
plan does not touch until week three. Every later phase then *thickens* a layer that
already exists instead of introducing a new one.

**Decisions taken, with rejected alternatives:**

| Decision | Chosen | Rejected because |
|---|---|---|
| Slice shape | Walking skeleton | Layer-by-layer (parent Phase 1) leaves the real-time seam unproven until week 3 and produces nothing demoable for six weeks |
| Seams proven | CRUD **and** one live tick | CRUD-only defers the hardest problem; real-time-only leaves EF Core and migrations untested |
| Pipeline reach | Local + green CI | Full AWS deploy roughly doubles the slice, starts the RDS bill now, and makes Terraform debugging dominate week 1 |
| Identity | Stubbed dev user | Real JWT + refresh + OIDC is a slice of its own; no-user-concept makes per-user query scoping a later retrofit |
| Runtime | **.NET 10**, not .NET 8 | .NET 8 reaches end-of-life in November 2026 — four months out — and only the .NET 10 SDK (10.0.302) is installed. Shipping a showcase on an EOL runtime is hard to defend; the parent README's .NET 8 references are amended in Task 11 of the plan |

---

## Scope boundary

### In scope

| Layer | Deliverable |
|---|---|
| Domain | `User`, `Watchlist` aggregate, `WatchlistItem`, `PriceTick` record |
| Application | 3 MediatR handlers (get watchlist, add item, remove item), FluentValidation on both commands, `IWatchlistRepository` interface |
| Infrastructure | EF Core `DbContext`, initial migration, repository implementation, reference-ticker + dev-user seed, `FakeTickService`, `TickBroadcaster` |
| Api | `/api/v1/watchlist` endpoints, `PriceHub`, `DevAuthMiddleware`, correlation-ID middleware, exception → ProblemDetails middleware |
| Frontend | `apps/dashboard` watchlist screen, `packages/api-client` (typed + zod-validated) |
| Local infra | `docker-compose.yml` (SQL Server 2022), API `Dockerfile`, `.gitignore` |
| CI | GitHub Actions: dotnet build/test, pnpm lint/typecheck/test, docker build — **no deploy** |
| Docs | ADR-001 (modular monolith + one service), ADR-002 (walking-skeleton-first sequencing), `TESTING.md` stub recording deliberate gaps, root `README.md` promoted from stub |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

RabbitMQ · outbox pattern · Alerts microservice · Dapper · Terraform · any AWS resource ·
real JWT/refresh/OIDC · real market data feed · Storybook · `packages/ui` ·
`packages/emitter` · `alerts-mfe` · Playwright/E2E · Lambda · OpenTelemetry ·
rate limiting · idempotency keys · CSP · price persistence/history.

### Two judgement calls

**`packages/api-client` is included** despite being a "later" concern. The parent README
makes "components never call `fetch` directly" an architectural rule. Retrofitting that
boundary means touching every component written before it, which is precisely the rework a
skeleton exists to prevent.

**MediatR is included.** Clean Architecture with an empty Application layer is not a
skeleton of this architecture — it is a skeleton of a different one. Three thin handlers
establish the shape.

**Zustand is excluded**, despite appearing in the parent stack table. Slice 1 has no
client-side UI state worth a store; TanStack Query covers the server cache. It arrives
when there is real UI state to hold, and ADR-007 will read better argued against an actual
problem than against an anticipated one.

---

## Architecture

### Repository layout

```
marketpulse-pro/
├── MarketPulse.sln
├── src/
│   ├── MarketPulse.Domain/          # User, Watchlist, WatchlistItem, PriceTick — zero deps
│   ├── MarketPulse.Application/     # Handlers, validators, repository interface
│   ├── MarketPulse.Infrastructure/  # DbContext, migration, repo impl, tick services
│   └── MarketPulse.Api/             # Controllers, PriceHub, middleware, Program.cs
├── apps/dashboard/                  # React 18 + Vite + TypeScript (strict)
├── packages/api-client/             # Typed client, zod schemas
├── tests/
│   ├── MarketPulse.UnitTests/       # xUnit
│   └── MarketPulse.IntegrationTests/# WebApplicationFactory + Testcontainers
├── docs/adr/
├── docker-compose.yml
└── .github/workflows/ci.yml
```

The dependency rule is enforced structurally by project references: `Domain` references
nothing; `Application` references `Domain`; `Infrastructure` references `Application`;
`Api` references `Infrastructure` and `Application`. A violation fails the build rather
than a review.

### Domain model

`Watchlist` is an aggregate root owning its items. Invariants enforced inside the
aggregate, not in handlers or controllers:

- A ticker may appear at most once (`AddItem` throws on duplicate)
- At most 20 items (`AddItem` throws when full)
- Items are only added or removed through the root; the item collection is exposed as
  `IReadOnlyCollection<WatchlistItem>`

`PriceTick` is an immutable `record` (`Ticker`, `Price`, `TimestampUtc`).

**Reference data.** A `Ticker` reference table is seeded with ~25 real ASX ETF codes
(IVV, NDQ, VHY, FANG, VAS, A200, VGS, IOZ, STW, …). The dev user's watchlist is seeded with
IVV / NDQ / VHY / FANG. Validation of "unknown ticker" is against the reference table, not
against the seeded watchlist — otherwise the 20-item cap would be unreachable through the
API and could only ever be exercised as a domain unit test.

### Data flow A — REST

```
<WatchlistScreen>
  └─ useWatchlist()                 TanStack Query
      └─ apiClient.watchlist        packages/api-client, zod-parsed at the boundary
          └─ GET /api/v1/watchlist
              └─ MediatR GetWatchlistQuery
                  └─ IWatchlistRepository → EF Core → SQL Server
```

`DevAuthMiddleware` populates `HttpContext.User` with the seeded dev user before handlers
run, so every query is genuinely `.Where(w => w.UserId == …)` scoped. **Slice 2 deletes
this middleware and issues real JWTs; no schema, query, or handler changes.**

The middleware is registered **only when `IHostEnvironment.IsDevelopment()`**, and startup
throws if it is somehow active outside Development. A stub that can reach production is a
vulnerability, not a shortcut.

### Data flow B — real-time

```
FakeTickService : BackgroundService
  │  random walk over every reference ticker, 1 tick/sec each
  ▼
Channel<PriceTick>                       ← the swap-seam
  ▼
TickBroadcaster : BackgroundService
  └─ IHubContext<PriceHub>.Clients.All.SendAsync("tick", …)
      ▼
   usePriceStream()                      @microsoft/signalr, reconnect + backoff
      └─ <PriceCell ticker="IVV" />      memoised, re-renders independently
```

The `Channel<T>` between producer and broadcaster is the one piece of deliberate structure
in an otherwise minimal slice, and it earns its place twice. Without it, Phase 2 rips out
the `BackgroundService` and rewires the hub. With it, Phase 2 swaps only the **producer** —
broadcaster, hub, client hook and cells are already load-bearing and unchanged. It is also
the exact `Channel<T>` producer–consumer pipeline the parent README stakes interview
category 1 on.

Both background services thread a `CancellationToken` end to end and shut down cleanly on
`StopAsync`.

**Consequence, accepted:** prices are not persisted in slice 1 and tick history does not
survive a restart. `PriceHistory` and the Dapper read path arrive with the real feed in
Phase 2.

---

## Error handling

### Backend taxonomy

Domain errors are raised by the aggregate and mapped once at the edge, not caught
per-handler:

| Failure | Response | Raised by |
|---|---|---|
| Duplicate ticker | `409` ProblemDetails, `type: /errors/duplicate-ticker` | `Watchlist.AddItem` |
| Watchlist full (>20) | `409`, `/errors/watchlist-full` | `Watchlist.AddItem` |
| Unknown ticker | `400`, `/errors/unknown-ticker` | FluentValidation against the `Ticker` reference table |
| Malformed body | `400` with per-field errors | FluentValidation pipeline behaviour |
| Unhandled | `500`, correlation ID in body, full detail logged only | `ExceptionHandlingMiddleware` |

All responses are RFC 7807 ProblemDetails and carry the correlation ID. Internal exception
detail never reaches the client.

### Frontend failure modes

Two states that skeletons typically skip, handled here as visible UI rather than console
warnings:

- **Disconnected** — the stream hook enters `reconnecting`; cells grey out and stop
  presenting a stale number as live. Reconnect uses exponential backoff.
- **Stale tick** — no update for a given ticker in 10 seconds → that cell dims
  independently of connection state.

The React client surfaces the correlation ID on error toasts. It costs nothing now and is
the thread the Phase 5 observability story pulls on.

---

## Testing

Trophy-shaped: thin at every level, but present at all of them.

| Level | Tooling | Covers |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants — duplicate rejected, 21st item rejected. Pure, no mocks |
| Integration | WebApplicationFactory + Testcontainers (real SQL Server) | POST item → GET returns it → DELETE removes it, against the real migration |
| Frontend integration | Vitest + RTL + MSW | Screen renders, optimistic add, 409 error path renders its message |
| Frontend unit | Vitest | Price-stream reducer — tick applied, stale transition, disconnect transition |

The integration test proves EF Core and the migration genuinely work; a mocked `DbContext`
would prove nothing. The stream reducer is extracted as a pure function specifically so the
real-time logic is testable without a live socket.

### Deliberately not tested (recorded in `TESTING.md`)

- **No E2E.** Playwright arrives with real auth in slice 2 — a journey through a fake login
  tests the fake.
- **No SignalR transport test.** That tests Microsoft's library, not ours. The reducer
  carries our logic.
- **No coverage threshold in CI.** A threshold over a four-file codebase drives noise, not
  quality. It arrives when there is a codebase to threshold.

---

## Done criteria

Slice 1 is complete when, from a clean clone:

- [ ] `docker compose up -d` → `dotnet run --project src/MarketPulse.Api` → `pnpm --filter dashboard dev` yields a working watchlist in the browser
- [ ] Adding IVV persists across an API restart
- [ ] Every ticker on the watchlist updates once per second in the UI
- [ ] Killing the API greys the cells; restarting it reconnects without a page refresh
- [ ] `dotnet test` passes, with Testcontainers starting a real SQL Server
- [ ] `pnpm test` passes
- [ ] CI is green on a pull request and publishes a Docker image
- [ ] ADR-001 and ADR-002 are committed, each with a rejected-alternatives section
- [ ] `TESTING.md` records the three deliberate gaps above
- [ ] Root `README.md` replaced with the real project README (currently a 14-byte stub; the real content lives at `docs/MarketPulse-Pro-README.md`)

Explicitly **not** required: any AWS resource, any login screen, any queue.

---

## Interview category coverage

Mapped against the fifteen categories in [intervew-aspects.md](../../intervew-aspects.md).
The "still owed" column is the point of this table — it makes the debt visible so later
slices are sequenced against real gaps rather than vibes.

*(Note: `intervew-aspects.md` line 9 says "fourteen categories" but enumerates fifteen; the
document acknowledges this discrepancy itself. Fifteen is used throughout, matching the
parent README.)*

| # | Category | Slice 1 banks | Still owed after slice 1 |
|---|---|---|---|
| 1 | **C# & .NET runtime internals** | `Channel<T>` producer–consumer pipeline; `async`/`await` and the task model; `CancellationToken` threaded end to end; `record PriceTick`; `BackgroundService` lifecycle | `Span<T>` allocation-free parsing; BenchmarkDotNet results; GC/Gen0–Gen2 investigation; LINQ-vs-loop benchmark; delegates/events bus |
| 2 | **JavaScript fundamentals** | Event loop and async via the SignalR client; reconnect with exponential backoff; closures in the stale-tick timer | `AbortController`; `Promise.allSettled` batching; prototypes; the standalone ES-module emitter package |
| 3 | **TypeScript** | Generics (`apiClient<T>`); zod runtime validation at the API boundary; discriminated union for stream state (`connected` / `reconnecting` / `stale`) narrowed with a `never` check | DTOs generated from the .NET OpenAPI spec; `Result<T, E>`; the full message union (`PriceTick \| AlertTriggered \| OrderEvent`) |
| 4 | **ASP.NET Core depth** | Middleware pipeline (correlation ID, exception → ProblemDetails); DI lifetimes; hosted/background services; controllers | Minimal APIs side by side with controllers; options pattern with startup validation; action filters; the captive-dependency postmortem (ADR-005) |
| 5 | **React depth** | Rendering model — memoised `PriceCell` re-rendering independently; hooks with strict cleanup discipline; TanStack Query server cache + optimistic update | List virtualisation; profiler-driven before/after optimisation; the Zustand/TanStack two-layer split (ADR-007) |
| 6 | **HTML/CSS & layout** | Semantic landmark structure only | Accessibility (ARIA live regions, command palette, focus states, axe); responsive design and container queries; CSS Grid shell; theming |
| 7 | **Web API design** | `/api/v1/` resource-oriented routes; RFC 7807 ProblemDetails mapped from a domain error taxonomy | AuthN/AuthZ (JWT, refresh, OIDC, policies); rate limiting; idempotency keys; documented versioning strategy |
| 8 | **Data access & SQL Server** | EF Core change tracking, migrations, query translation; per-user query scoping | Dapper read path and the EF-vs-Dapper write-up; the N+1 postmortem (ADR-006); indexes and execution plans; transactions and isolation levels |
| 9 | **Browser, network & web performance** | *Nothing — deliberate* | All of it: caching strategy, CORS, cookies vs storage, Core Web Vitals/RUM, bundle optimisation, the measurement log |
| 10 | **Architecture & system design** ⭐ | Clean Architecture layering enforced by project references; CQRS via MediatR; DDD-lite aggregate with invariants; the `api-client` API layer; ADR-001 and ADR-002 with rejected alternatives | The modular-monolith-plus-one-service decision *realised* (currently only documented); micro-frontends; design system; the CQRS-was-overkill rollback (ADR-004) |
| 11 | **Distributed systems & messaging** | *Nothing.* The `Channel<T>` is in-process — that is category 1, not category 11 | All of it: RabbitMQ topology, outbox, idempotent consumers, eventual consistency, Polly resilience, the chaos test |
| 12 | **Cloud & DevOps** | Docker; docker-compose; GitHub Actions pipeline (lint → typecheck → test → build) | Every AWS resource; Terraform; IAM; blue-green deploys; OpenTelemetry and structured logging; cost model |
| 13 | **Security** | FluentValidation on all inputs; parameterised queries via EF Core; dev-auth stub fenced to the Development environment | Secure auth flows; secrets management; CSRF; CSP; XSS/DOMPurify; least-privilege IAM; the threat model |
| 14 | **Testing** | xUnit; WebApplicationFactory; Testcontainers against real SQL Server; Vitest; RTL; MSW; deliberate gaps recorded rather than hidden | E2E/Playwright; TDD applied to the alert-evaluation engine; coverage philosophy in practice |
| 15 | **Engineering practice** | Conventional commits; self-PR with description and review checklist; ADRs as written communication | Five of the six STAR stories (all depend on later-phase incidents); the incident-handling narrative |

**Summary:** slice 1 meaningfully advances 12 of 15 categories and fully completes none.
Categories 9, 11 and 12 are barely touched by design — they belong to later phases where
there is something real to measure, distribute, and deploy. Category 10 is the strongest
outcome, which is appropriate given the parent README identifies it as the largest senior
differentiator.

---

## What comes next

Slice 1 does not end the program; it ends the *first* sub-project. Candidate slice 2 is
real authentication (JWT + refresh + OIDC), which deletes `DevAuthMiddleware`, unblocks
Playwright, and banks most of category 7. That gets its own spec.
