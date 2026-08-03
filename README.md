# MarketPulse Pro 📈

**A full-stack, real-time ASX ETF portfolio & alerts platform — built to help everyday investors follow their money**

> Users register, build watchlists and mock portfolios, receive real-time prices over WebSocket, and set price alerts that are evaluated server-side and delivered through a message queue. Everything an investor needs to keep an eye on a portfolio without watching a screen all day.

[![CI](https://img.shields.io/badge/CI-GitHub_Actions-blue)]() [![Backend](https://img.shields.io/badge/.NET-10.0-512BD4)]() [![Frontend](https://img.shields.io/badge/React-18-61DAFB)]() [![IaC](https://img.shields.io/badge/Terraform-AWS-844FBA)]() [![License](https://img.shields.io/badge/license-MIT-green)]()

**Author:** Billy ([BetterCallBill](https://github.com/BetterCallBill)) · Sydney, Australia

---

## Table of contents

- [Why this project exists](#why-this-project-exists)
- [System overview](#system-overview)
- [Architecture](#architecture)
- [Tech stack](#tech-stack)
- [Feature overview](#feature-overview)
- [Engineering coverage map](#engineering-coverage-map)
- [Repository structure](#repository-structure)
- [Getting started](#getting-started)
- [Delivery plan](#delivery-plan)
- [Success criteria](#success-criteria)
- [Documentation index](#documentation-index)

---

## Why this project exists

Most retail investors track their holdings in a spreadsheet that is stale the moment they close it, and find out a price moved only after it has already moved. MarketPulse Pro exists to close that gap: **a live view of what you hold, and a server-side alert that reaches you whether or not the app is open**.

The product domain (ASX ETFs: IVV, NDQ, VHY, FANG) comes from genuine personal investing experience, so the features are the ones an investor actually reaches for — not a toy demo. Every engineering decision here is in service of that: prices are useless if they're late, alerts are worse than nothing if they're missed or duplicated, and a portfolio people trust cannot lose a transaction.

**Design principles:**

1. **Measure before optimising** — every performance change has a recorded baseline
2. **Document every trade-off** — significant decisions live in Architecture Decision Records
3. **Defend every dependency** — nothing in `package.json` or `.csproj` without a reason
4. **Fail on purpose, then fix** — engineered incidents (N+1, captive dependency, queue outage) become documented war stories

---

## System overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                          CloudFront + S3                            │
│                    React 18 / TypeScript SPA                        │
└───────────────┬─────────────────────────────┬───────────────────────┘
                │ HTTPS (REST /api/v1)        │ WebSocket (SignalR)
                ▼                             ▼
┌─────────────────────────────────────────────────────────────────────┐
│                     ASP.NET Core 10 — ECS Fargate                   │
│  ┌──────────────────────┐   ┌──────────────────────┐                │
│  │  Portfolio Module    │   │  Market Data Module  │  Modular       │
│  │  (Controllers, EF)   │   │  (Minimal APIs,      │  Monolith      │
│  │                      │   │   Ingestion Service) │                │
│  └──────────┬───────────┘   └──────────┬───────────┘                │
│             │        Outbox            │ publish PriceTick          │
└─────────────┼───────────────────────────┼──────────────────────────┘
              ▼                           ▼
     ┌────────────────┐          ┌─────────────────┐
     │  RDS SQL Server│          │    RabbitMQ     │
     └────────────────┘          └────────┬────────┘
                                          │ consume (idempotent)
                                          ▼
                              ┌───────────────────────┐
                              │   Alerts Microservice │
                              │   (.NET 10 Worker)    │
                              └───────────────────────┘

     ┌────────────────┐          ┌─────────────────────────┐
     │ Lambda (EOD    │          │ OpenTelemetry → Datadog │
     │ price snapshot)│          │ Serilog structured logs │
     └────────────────┘          └─────────────────────────┘
```

**The core architectural decision:** a **modular monolith (Portfolio + Market Data) plus one extracted Alerts microservice**. Alert evaluation is the one workload whose load is driven by how many thresholds users set rather than by how many people are looking at the app, so it scales independently; everything else stays in one deployable. Trade-offs of both approaches are documented in [ADR-001](docs/adr/001-modular-monolith-plus-one-service.md).

---

## Architecture

### Backend — Clean Architecture + CQRS

```
src/
├── MarketPulse.Domain/          # Entities, aggregates, domain events — zero dependencies
├── MarketPulse.Application/     # CQRS handlers (MediatR), interfaces, validation
├── MarketPulse.Infrastructure/  # EF Core, Dapper, RabbitMQ, external feed clients
└── MarketPulse.Api/             # Controllers, minimal APIs, middleware, SignalR hubs
```

- **Dependency rule:** all dependencies point inward; Domain knows nothing about Infrastructure
- **CQRS via MediatR** for Portfolio commands/queries — with [ADR-004](docs/adr/004-cqrs-scope.md) honestly documenting where CQRS was overkill and rolled back
- **DDD-lite aggregates:** `Portfolio`, `Watchlist` enforce invariants; domain events drive the outbox

### Frontend — Feature-sliced monorepo

```
apps/dashboard          # Main React app
apps/alerts-mfe         # Module Federation micro-frontend (alerts UI)
packages/ui             # Design system + Storybook
packages/api-client     # Typed API layer, zod-validated, OpenAPI-generated DTOs
packages/emitter        # Standalone ES module event emitter
```

- **Two-layer state architecture:** server state in TanStack Query, client/UI state in Zustand — rationale in [ADR-007](docs/adr/007-state-architecture.md)
- Components never call `fetch` directly — all data access flows through `packages/api-client`

### Messaging — eventual consistency done properly

- **Outbox pattern** on the publisher: domain events written transactionally with state, relayed to RabbitMQ by a background dispatcher
- **Idempotent consumers:** the Alerts service dedupes on message ID; redelivery is safe
- **Resilience:** Polly retry + circuit breaker around the external market data feed; chaos test kills RabbitMQ mid-flow and verifies recovery

---

## Tech stack

| Layer | Technology |
|---|---|
| **Backend** | .NET 10, ASP.NET Core, MediatR, FluentValidation, SignalR |
| **Data** | SQL Server (RDS), EF Core 10, Dapper (read-heavy paths) |
| **Messaging** | RabbitMQ, outbox pattern, Polly |
| **Frontend** | React 18, TypeScript (strict), Vite, TanStack Query, Zustand, zod |
| **Design system** | Storybook, CSS custom properties, @tanstack/react-virtual |
| **Testing** | xUnit, NSubstitute, WebApplicationFactory, Testcontainers, Vitest, RTL, MSW, Playwright |
| **Cloud** | AWS — ECS Fargate, Lambda, RDS, S3 + CloudFront, Secrets Manager, IAM |
| **IaC & CI/CD** | Terraform, GitHub Actions, Docker, blue-green deploys via ECS |
| **Observability** | OpenTelemetry, Serilog, Datadog / CloudWatch |

---

## Feature overview

| Feature | Description |
|---|---|
| **Live price board** | Real-time grid via SignalR/WebSocket; server-side ingestion fans out price ticks |
| **Watchlist & mock portfolio** | CRUD watchlist, simulated buy/sell with idempotency keys, P&L calculation |
| **Price alerts** | Threshold alerts evaluated server-side by the Alerts microservice via RabbitMQ |
| **Historical charts** | Lazy-loaded charting; Dapper-backed history queries with `stale-while-revalidate` caching |
| **Portfolio notes** | Rich-text notes per holding, sanitised with DOMPurify |
| **Auth** | JWT + refresh tokens, OIDC social login, httpOnly + SameSite cookies, CSRF tokens |
| **EOD snapshot** | Scheduled Lambda captures end-of-day prices to demonstrate serverless compute |
| **Command palette** | Keyboard-first navigation (`Cmd/Ctrl + K`), fully accessible |
| **Dark / light theme** | CSS custom properties, respects `prefers-color-scheme` and `prefers-reduced-motion` |

---

## Engineering coverage map

### Tier 1 — Language & runtime fundamentals

#### 1. C# language & .NET runtime internals

**Where:** `MarketPulse.Infrastructure/Ingestion/PriceIngestionService.cs` and the alert-evaluation engine

- **async/await & task model:** `Channel<T>` producer–consumer pipeline; `IAsyncEnumerable<PriceTick>` streaming; cancellation tokens threaded end-to-end
- **`Span<T>`/performance:** allocation-free parsing of the market data feed; benchmarked with **BenchmarkDotNet**, results committed to `docs/benchmarks/`
- **Records & pattern matching:** immutable domain events (`record PriceTick`); exhaustive `switch` expressions in the alert-evaluation engine
- **Delegates/events:** internal price-tick bus before messages reach the queue
- **Memory & GC:** documented investigation of Gen0/Gen2 behaviour under sustained ingestion load
- **LINQ internals:** a benchmarked LINQ-vs-loop hot path with the trade-off written up

#### 2. JavaScript fundamentals & language internals

**Where:** `apps/dashboard/src/lib/` and `packages/emitter/`

- **Event loop & async:** WebSocket client with automatic reconnect and exponential backoff; `AbortController` for cancellable requests; `Promise.allSettled` for batched quote fetches
- **Closures:** debounced ticker search, rate-limiter utility — closures doing production work
- **Prototypes & ES modules:** `@marketpulse/emitter` — a tiny event emitter written with prototypes/classes, shipped as a standalone tree-shakeable ES module package

#### 3. TypeScript

**Where:** `packages/api-client/` and the WebSocket message handler

- **Discriminated unions:** `PriceTick | AlertTriggered | OrderEvent`, narrowed exhaustively with `never` checks
- **Generics:** typed `apiClient<T>`, generic `useAsync<T>` hook, reusable `Result<T, E>`
- **Runtime validation:** zod schemas at every API boundary — "types end at compile time"
- **Full-stack types:** frontend DTOs **generated from the .NET OpenAPI spec** — a contract shared across the stack

### Tier 2 — Framework & platform depth

#### 4. .NET 10 / ASP.NET Core framework depth

**Where:** `MarketPulse.Api/`

- **Middleware pipeline:** custom correlation-ID middleware; global exception → ProblemDetails middleware
- **DI & lifetimes:** all three lifetimes used deliberately; [ADR-005](docs/adr/005-captive-dependency-postmortem.md) documents an engineered captive-dependency bug and its fix
- **Options pattern:** strongly-typed feed configuration with validation on startup
- **Minimal APIs vs controllers:** Market Data module uses minimal APIs, Portfolio uses controllers — both styles live side by side for direct comparison
- **Filters:** validation action filters on Portfolio endpoints
- **Hosted/background services:** `PriceIngestionService` (`BackgroundService`) and the outbox dispatcher

#### 5. Frontend framework depth (React)

**Where:** `apps/dashboard/`

- **Rendering model:** dozens of live-updating price cells; `React.memo` boundaries and stable selectors chosen from **profiler data**, before/after documented
- **State management:** TanStack Query (server cache, optimistic updates, invalidation) + Zustand (UI state) — deliberate two-layer split
- **Hooks/lifecycle:** `usePriceStream`, `useAsync`, `useDebouncedValue` with strict cleanup discipline
- **Performance patterns:** list virtualisation on the watchlist (`@tanstack/react-virtual`)

#### 6. HTML/CSS & layout

**Where:** `apps/dashboard/` shell and `packages/ui/`

- **Semantics:** landmark structure (`header`/`nav`/`main`/`aside`), audited heading hierarchy
- **Accessibility:** keyboard-navigable command palette, ARIA live regions announcing alert triggers, visible focus states, WCAG AA contrast in both themes, axe + screen-reader tested
- **Responsive design:** container queries and fluid grid tracks, mobile → ultrawide
- **Flexbox/grid:** CSS Grid for the dashboard shell, flexbox inside cards — each where it's the right tool

#### 7. Web API design

**Where:** `MarketPulse.Api/` + `docs/api-style-guide.md`

- **REST maturity & versioning:** resource-oriented `/api/v1/` endpoints with a documented versioning strategy
- **AuthN/AuthZ:** JWT access + refresh tokens, OIDC social login, policy-based authorization
- **Rate limiting:** .NET 10 built-in `RateLimiter` middleware with per-user partitions
- **Idempotency:** idempotency keys on order/alert creation, stored and replayed server-side
- **Error handling:** RFC 7807 ProblemDetails everywhere, mapped from a domain error taxonomy

#### 8. Data access & SQL Server

**Where:** `MarketPulse.Infrastructure/Persistence/` + `docs/sql/`

- **EF Core:** change tracking, migrations, query translation; a **deliberately created then fixed N+1** documented in [ADR-006](docs/adr/006-n-plus-one-postmortem.md)
- **Dapper trade-off:** read-heavy price-history queries on Dapper, with the EF-vs-Dapper decision written up
- **Indexing & execution plans:** covering indexes with **before/after execution plans** committed to `docs/sql/`
- **Transactions & isolation:** serializable vs read-committed demonstrated in the mock order placement flow, with anomaly tests

### Tier 3 — Cross-cutting engineering

#### 9. Browser, network & web performance

**Where:** frontend + BFF configuration + `docs/PERFORMANCE.md`

- **Cookies vs storage:** httpOnly + SameSite session cookies keep session tokens out of reach of injected scripts; `localStorage` holds UI preferences only
- **CORS:** configured on the API with a written explanation of preflight behaviour
- **Caching:** immutable hashed assets; `stale-while-revalidate` for chart history
- **Core Web Vitals:** RUM snippet reports LCP / INP / CLS from production
- **Bundle optimisation:** route-based code splitting; charting library lazy-loaded and prefetched on intent; bundle analysed with `rollup-plugin-visualizer`; dependency budget enforced in CI
- **Measurement-first:** `PERFORMANCE.md` logs a baseline before every optimisation — no exceptions

#### 10. Architecture & system design ⭐

**Where:** the entire system shape + `docs/adr/`

- **Backend:** Clean/Onion layering, CQRS via MediatR (with an honest "where it was overkill" ADR), DDD-lite aggregates, SOLID and dependency inversion enforced by project references
- **The flagship decision:** modular monolith + one extracted microservice — both sides of the trade-off defensible from experience
- **Frontend:** feature-sliced monorepo, Storybook design system, API-layer package, deliberate state architecture (server cache / client state / URL state)
- **Micro-frontends:** Module Federation spike extracting the alerts UI — minimal but real
- **When *not* to use patterns:** every ADR includes a "rejected alternatives" section

#### 11. Distributed systems & messaging

**Where:** `MarketPulse.Alerts/` worker + RabbitMQ topology

- **Monolith vs microservices:** one service extracted for a documented reason (independent scaling of alert evaluation), everything else kept in a single deployable
- **RabbitMQ:** topic exchange for price events; dead-letter queue for poison messages
- **Outbox pattern:** domain events persisted transactionally, relayed by a background dispatcher
- **Idempotent consumers:** dedupe on message ID; redelivery-safe by design
- **Eventual consistency:** alert notification flow documented end-to-end
- **Resilience:** Polly retry with jitter + circuit breaker around the external feed; a **chaos test** kills RabbitMQ mid-flow and asserts recovery

#### 12. Cloud & DevOps

**Where:** `infra/` (Terraform) + `.github/workflows/`

- **Compute — both models on purpose:** ECS Fargate for the API and Alerts worker; **Lambda** for the scheduled EOD snapshot
- **Storage & networking:** RDS SQL Server, S3 + CloudFront for the SPA, VPC with private subnets for data services
- **IAM:** task roles with least privilege; no long-lived credentials
- **Cost awareness:** `docs/cost-model.md` estimates monthly spend and the free-tier strategy
- **IaC:** everything provisioned via **Terraform**, state in S3 with locking
- **CI/CD:** GitHub Actions — lint → typecheck → unit → integration (Testcontainers) → E2E (Playwright) → Docker build → **blue-green deploy** via ECS
- **Observability:** OpenTelemetry traces + structured Serilog logs shipped to Datadog/CloudWatch; correlation IDs propagate from browser → API → queue → worker

#### 13. Security (full-stack)

**Where:** across the stack + `docs/THREAT-MODEL.md`

- **Backend:** FluentValidation on all inputs; parameterised queries only; AWS Secrets Manager for credentials; JWT best practices (short-lived access, rotating refresh); least-privilege IAM
- **Frontend:** DOMPurify sanitisation on portfolio notes (the one rich-text surface); CSRF tokens on all mutations; an **authored Content-Security-Policy** header with a commented policy file — no `unsafe-inline` in production
- **Threat model:** `THREAT-MODEL.md` covers attack surface and mitigations for both halves, OWASP API Top 10 aligned

#### 14. Testing (full-stack)

**Where:** `tests/` (backend), `apps/dashboard/src/**/*.test.tsx`, `e2e/` + `docs/TESTING.md`

- **Backend unit:** xUnit + NSubstitute for domain and application logic
- **Backend integration:** `WebApplicationFactory` API tests; **Testcontainers** spins up real SQL Server and RabbitMQ in CI — no mocked databases in integration tests
- **Frontend unit:** Vitest for utilities, hooks, and the emitter package
- **Frontend integration:** React Testing Library + **MSW** mocking the HTTP/WebSocket layer
- **E2E:** Playwright — login, add-to-watchlist, create-alert journeys, headless in CI on every PR
- **Strategy:** trophy-shaped distribution; `TESTING.md` explains what is deliberately *not* tested and why; coverage tracked but not worshipped; TDD used for the alert-evaluation engine

### Tier 4 — Professional practice

#### 15. Engineering practice & behavioural

**Where:** the repo's history and `docs/`

- **CI/CD discipline:** every feature lands via a self-PR with description, screenshots, and a review checklist; conventional commits throughout
- **Code review:** PR templates enforce "what/why/how tested"; the history reads like a well-run team's
- **Documentation as communication:** ADR folder, architecture-first README, style guides — written cross-team communication demonstrated
- **Six engineering write-ups with real metrics:**
  1. The N+1 hunt (data access + measurement)
  2. The captive-dependency bug (DI depth)
  3. The queue-outage chaos test → *"tell me about a production issue"*
  4. The CQRS-was-overkill rollback (judgement, not dogma)
  5. The blue-green deployment setup (DevOps)
  6. The performance measurement log (measure-first mindset)

---

## Repository structure

```
marketpulse-pro/
├── src/
│   ├── MarketPulse.Domain/            # Entities, aggregates, domain events
│   ├── MarketPulse.Application/       # CQRS handlers, interfaces, validation
│   ├── MarketPulse.Infrastructure/    # EF Core, Dapper, RabbitMQ, feed clients
│   ├── MarketPulse.Api/               # Controllers, minimal APIs, SignalR, middleware
│   └── MarketPulse.Alerts/            # Extracted alerts microservice (.NET Worker)
├── apps/
│   ├── dashboard/                     # Main React app
│   └── alerts-mfe/                    # Module Federation micro-frontend
├── packages/
│   ├── ui/                            # Design system + Storybook
│   ├── api-client/                    # Typed API layer (zod + OpenAPI-generated DTOs)
│   └── emitter/                       # Standalone ES module event emitter
├── tests/
│   ├── MarketPulse.UnitTests/         # xUnit + NSubstitute
│   ├── MarketPulse.IntegrationTests/  # WebApplicationFactory + Testcontainers
│   └── e2e/                           # Playwright suites
├── infra/                             # Terraform (ECS, Lambda, RDS, S3, IAM, VPC)
├── docs/
│   ├── adr/                           # Architecture Decision Records
│   ├── sql/                           # Execution plans, index analysis
│   ├── benchmarks/                    # BenchmarkDotNet results
│   ├── PERFORMANCE.md                 # Before/after measurement log
│   ├── THREAT-MODEL.md                # Full-stack threat model
│   ├── TESTING.md                     # Test strategy & rationale
│   ├── api-style-guide.md             # REST conventions
│   └── cost-model.md                  # AWS cost estimate & free-tier strategy
└── .github/workflows/                 # CI/CD pipelines
```

---

## Getting started

### Slices 1–2 — what actually runs today

These four commands are the real, runnable path on this branch. The "Local development"
block further down describes the target shape for later phases (an Alerts worker,
RabbitMQ) — none of that exists yet, so don't run it expecting it to work.

```bash
# 1. Start infrastructure (SQL Server)
docker compose up -d

# 2. Apply database migrations (creates the schema and seeds reference tickers +
#    the dev user's watchlist; safe to re-run)
dotnet ef database update --project src/MarketPulse.Infrastructure

# 3. Start the API (serves REST + the SignalR hub) — http://localhost:5100
dotnet run --project src/MarketPulse.Api

# 4. Start the dashboard — http://localhost:5173
pnpm install && pnpm --filter @marketpulse/dashboard dev
```

The dashboard talks to the API at `http://localhost:5100` by default; override with the
`VITE_API_URL` environment variable if you run the API on a different port.

#### Signing in

The app requires a real account — every route but `/login` and `/register` is closed, and
the API rejects unauthenticated requests. There are two ways in at
`http://localhost:5173/login`:

| | Email | Password |
|---|---|---|
| **Seeded dev account** | `dev@marketpulse.local` | `DevPassw0rd!2026` |
| **A new account** | anything you like, via `/register` | at least 12 characters |

The dev account is a development convenience so a clean clone has something to look at: it
owns the four seeded tickers, and its password hash is a committed constant because EF
Core's `HasData` requires determinism. The hardening slice removes it — do not deploy this
seed anywhere real.

A newly registered account starts with an **empty** watchlist. That is correct, not a bug:
add a ticker and it begins receiving live prices immediately.

Sessions live in `httpOnly` cookies, so a page reload keeps you signed in and no token is
reachable from JavaScript. See
[ADR-003](docs/adr/003-cookie-based-sessions.md) for why, and for what that costs at
deployment time.

### Prerequisites

- .NET 10 SDK · Node.js 20+ · pnpm · Docker Desktop

### Local development (aspirational — describes later phases, not yet runnable)

```bash
# 1. Start infrastructure (SQL Server + RabbitMQ)
docker compose up -d

# 2. Run database migrations
dotnet ef database update --project src/MarketPulse.Infrastructure

# 3. Start the API (serves SignalR hub + REST)
dotnet run --project src/MarketPulse.Api

# 4. Start the Alerts worker
dotnet run --project src/MarketPulse.Alerts

# 5. Start the frontend
pnpm install && pnpm --filter dashboard dev
```

### Running tests

```bash
dotnet test                                  # Backend unit + integration (Testcontainers)
pnpm test                                    # Frontend unit + integration (Vitest + MSW)
pnpm typecheck                               # tsc --noEmit across every workspace package

# E2E journeys (Playwright + Chromium). Needs SQL Server up and a production
# dashboard build — Playwright starts the API and the preview server itself.
pnpm --filter @marketpulse/e2e exec playwright install chromium   # once
docker compose up -d
pnpm --filter @marketpulse/dashboard build
pnpm e2e
```

See [docs/TESTING.md](docs/TESTING.md) for what each level covers and which gaps are
deliberate.

---

## Delivery plan

| Phase | Weeks | Deliverable | Areas exercised |
|---|---|---|---|
| 1 · Backend foundation | 1–2 | Clean Architecture skeleton, auth, Portfolio CRUD, EF Core + migrations | 1, 4, 7, 8, 13 |
| 2 · Real-time core | 3–4 | Price ingestion service, SignalR fan-out, Dapper history queries | 1, 4, 8, 11 |
| 3 · Messaging & alerts | 5–6 | RabbitMQ, outbox, Alerts microservice, chaos test — **alerts that survive an outage** | 10, 11, 14 |
| 4 · Frontend core | 7–8 | Dashboard, price grid, watchlist, state architecture, design system | 2, 3, 5, 6 |
| 5 · Cloud & pipeline | 9–10 | Terraform, ECS blue-green, Lambda snapshot, observability | 12, 15 |
| 6 · Hardening | 11–12 | Full test suite, CSP, threat model, performance pass, RUM, docs | 9, 13, 14, 15 |

> **Pragmatic path:** backend first (weeks 1–6) — prices, portfolios, and alerts are what make the product useful, so the engine is trustworthy before the dashboard gets polished.

---

## Success criteria

- [ ] Lighthouse ≥ 95 across all categories on the dashboard route
- [ ] LCP < 2.5s · INP < 200ms · CLS < 0.1 (field data via RUM)
- [ ] Zero critical axe accessibility violations
- [ ] CSP with no `unsafe-inline` in production
- [ ] Backend integration tests run against **real** SQL Server + RabbitMQ (Testcontainers) in CI
- [ ] Chaos test: RabbitMQ outage recovers with zero lost alerts
- [ ] p95 API latency < 200ms under ingestion load (documented load test)
- [ ] Every significant decision has an ADR with rejected alternatives
- [ ] Six engineering write-ups documented with real metrics
- [ ] Infra reproducible from scratch with `terraform apply`

---

## Documentation index

| Document | Purpose |
|---|---|
| [docs/adr/](docs/adr/) | Architecture Decision Records — every significant choice, with rejected alternatives |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | Measurement-first optimisation log (before/after) |
| [docs/THREAT-MODEL.md](docs/THREAT-MODEL.md) | Full-stack threat model, OWASP-aligned |
| [docs/TESTING.md](docs/TESTING.md) | Test strategy, trophy rationale, deliberate gaps |
| [docs/api-style-guide.md](docs/api-style-guide.md) | REST conventions, versioning, error taxonomy |
| [docs/cost-model.md](docs/cost-model.md) | AWS cost estimate and free-tier strategy |
| [docs/sql/](docs/sql/) | Execution plans and index analysis |
| [docs/benchmarks/](docs/benchmarks/) | BenchmarkDotNet results |

---

*Built for people who invest their own money — every feature answers a question an investor actually asks, and every incident made the platform more dependable.*
