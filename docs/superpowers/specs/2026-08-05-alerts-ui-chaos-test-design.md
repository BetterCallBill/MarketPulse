# Slice 4b — Alerts UI and the chaos test: "An alert you can see, and an outage you can prove"

**Date:** 2026-08-05
**Depends on:** slice 4a (alerts pipeline backend, merged)
**Branch:** `feature/slice-4b-alerts-ui-chaos-test` → `test`

---

## Why this slice exists

Slice 4a built the pipeline: rules evaluated in a separate worker, an outbox on
`AlertTriggered`, publisher confirms, an idempotent consumer, per-user SignalR delivery. None
of it is visible in the running application, and the README's headline claim — alerts that
survive a broker outage — is asserted by `BrokerOutageTests` at the component level but has
never been demonstrated across the real processes. The 4a spec closed by naming both gaps as
this slice: *"4b takes this pipeline and makes it visible and provable."*

Visible means the alerts UI and notifications panel on `packages/ui` and the Playwright
journey that walks them. Provable means the chaos test: kill RabbitMQ mid-flow, restart it,
and show that exactly one notification lands. This slice also settles the client-state
question the roadmap has carried since slice 3 — whether Zustand ever appears — and picks up
the two 4a review findings worth fixing while the messaging code is fresh.

### Decisions taken, with rejected alternatives

| Decision | Chosen | Rejected because |
|---|---|---|
| Client-state layer | **Server cache only. TanStack Query holds all server state; SignalR pushes patch that cache; the unread badge is derived from it. ADR-007 is written to say exactly this, and the README's "TanStack Query + Zustand" claim is corrected** | Adding Zustand fulfils the README as written, but unread state turns out to be server cache in disguise — the server already owns `IsRead` (`POST /api/v1/notifications/{id}/read`), so a client store would be a second copy of data with an invalidation problem, added to satisfy a document. The roadmap warned against exactly that; the honest move is to rewrite the claim |
| UI surface | **A notification bell with an unread badge in the dashboard header, opening a dropdown panel; alert rules created and managed inline on watchlist rows** | A dedicated `/alerts` route is clearer separation but a second screen to build, style, route-guard and test — beyond the ~8-task slice shape, and alerts divorced from the prices they watch. A `/notifications` page as well is the same again. Inline keeps the rule beside the live price it references |
| Chaos test harness | **A .NET integration test composing the real API host and the real `MarketPulse.Alerts` worker against Testcontainers SQL Server + RabbitMQ, stopping the broker container mid-flow** | Driving the outage from Playwright (a helper script running `docker compose stop rabbitmq`) is maximally end-to-end, but couples the e2e suite to Docker control, is slower and flakier, and proves nothing the .NET orchestration doesn't — the browser's part of the story (notification appears in the panel) is already the happy-path journey's job |
| 4a carryover | **The two messaging fixes land here: the publisher dispose race and the consumer backoff floor** | Taking all five findings drags observability-slice work (the unbounded requeue loop) into a UI slice; taking none means the chaos test exercises code with known, already-diagnosed defects. The two chosen are small, touch code this slice tests anyway, and were flagged "worth fixing while fresh" |
| Notification stream client | **A separate `useNotificationStream` hook against `/hubs/notifications`, mirroring `usePriceStream`'s shape and reconnect policy** | Multiplexing both hubs through one connection manager is an abstraction with two call sites; the existing hook's shape (infinite-reconnect policy, zod-parse on receive, reducer state) is proven and the parallel structure keeps "prices are public, notifications are private" visible in the client too |
| Marking read | **Opening the panel marks the visible unread rows read, one `POST {id}/read` each, optimistically patched into the cache** | A bulk `mark-all-read` endpoint is nicer but is new API surface 4a deliberately didn't build; adding it now for a panel that shows ~50 rows maximum is premature. Explicit per-row "mark read" buttons push bookkeeping onto the user for no benefit |

---

## Scope boundary

### In scope

| Layer | Deliverable |
|---|---|
| `packages/api-client` | zod schemas + client methods for `GET/POST/DELETE /api/v1/alerts`, `POST /api/v1/alerts/{id}/rearm`, `GET /api/v1/notifications`, `POST /api/v1/notifications/{id}/read`; a `notificationSchema` shared with the hub payload |
| `packages/ui` | `Badge` primitive (unread count, token-styled); notifications panel composed from the existing `Panel`; anything else only if a watchlist-row alert control genuinely needs it |
| `apps/dashboard` | `features/alerts`: `useAlerts` (TanStack Query), inline rule control on watchlist rows (create with direction + threshold, show Active/Triggered state, re-arm, delete). `features/notifications`: `useNotificationStream` (SignalR, `/hubs/notifications`), `useNotifications` (query + mark-read mutations), header bell with derived unread badge, dropdown panel |
| Backend fixes | `RabbitMqEventPublisher.DisposeAsync` acquires `_gate` before disposing (copying `RabbitMqConnection`'s shape) and its comment corrected; `RabbitMqConsumerService` keeps a one-delay floor after a shutdown-triggered resubscribe |
| Chaos test | New integration test class: real API host + real Alerts worker, Testcontainers SQL Server + RabbitMQ, broker stopped after the rule marks Triggered and before the outbox dispatches, restarted, exactly one notification row asserted |
| e2e | `alerts.spec.ts` journey (register → watch ticker → set alert → tick crosses → badge increments → panel shows notification → mark read); harness gains RabbitMQ as a prerequisite and starts the Alerts worker from Playwright global-setup |
| Docs | **ADR-007** (state architecture: server cache only, Zustand as rejected alternative); README two-layer claim corrected; ROADMAP rows updated; `TESTING.md` gains the chaos test |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

A dedicated `/alerts` or `/notifications` route · `mark-all-read` API surface · percentage-change,
volume or moving-average rule types · email, SMS or web-push channels (the README's "whether or
not the app is open" still ends at the websocket plus the durable row) · the unbounded transient
requeue loop and redelivery counters (observability slice) · the in-flight-ack log-noise and
test-queue-hygiene findings from 4a · pagination UI beyond the server's `?take=`/`?skip=`
(the panel shows the newest 50) · toast/inline popup notifications (the badge is the signal;
a toast layer is polish this slice does not need) · Zustand.

---

## Architecture

### What does not change

The entire 4a pipeline is consumed as-is. No new endpoints, no contract changes, no migration.
The API's `AlertTriggeredConsumer` already persists the notification row and pushes
`"notification"` to `Clients.User(userId)` on `NotificationHub`; the worker already evaluates,
marks Triggered under `RowVersion` concurrency, and dispatches through the outbox. This slice
is a client of all of it — which is itself the test that 4a's API surface was right.

### Frontend structure

Two new feature folders in `apps/dashboard/src/features/`, shaped like the existing three:

- **`alerts/`** — `useAlerts.ts` wraps the four rule endpoints in TanStack Query
  (`['alerts']` key). The watchlist row gains an alert control: a compact form (direction +
  threshold) to create, and a status chip (Active/Triggered, from `AlertRuleDto.Status`) with
  re-arm and delete actions. Creation, deletion and re-arm are mutations that invalidate
  `['alerts']`.
- **`notifications/`** — `useNotificationStream.ts` opens `/hubs/notifications` with the same
  infinite-reconnect policy as `usePriceStream`, zod-parses each `"notification"` payload, and
  hands it to a callback. `useNotifications.ts` owns the query (`['notifications']`) and the
  mark-read mutation. The stream callback **prepends the pushed notification into the
  `['notifications']` cache** via `setQueryData` — the push is the optimisation, the row is
  the truth, so a reconnect-triggered refetch converges to the same state. It also invalidates
  `['alerts']`, because a notification implies some rule just flipped to Triggered.

The unread badge count is `notifications.filter(n => !n.isRead).length` — derived, never
stored. Panel-open is `useState` in the header component; it is ephemeral view state, not
client state, and needs no store. This is the whole ADR-007 argument in two sentences.

### Marking read

Opening the panel fires the mark-read mutation for each visible unread row, optimistically
setting `isRead: true` in the cache; a failed POST rolls back on the next refetch. The
server's per-row endpoint is the unit of truth; no client-side "seen" concept exists apart
from it.

### `packages/ui`

One new primitive: `Badge` — a small count indicator styled from tokens, with a
`VisuallyHidden` label ("3 unread notifications") for screen readers. The panel itself is the
existing `Panel` primitive in a positioned popover container owned by the dashboard header;
if the positioning wrapper proves generic it may graduate to `packages/ui` later, but it is
not designed as a library component now.

### Layering

`DependencyRuleTests` continues to hold: nothing new in Application or Domain. The dashboard
continues to reach the API exclusively through `packages/api-client` — the new endpoints get
schemas there, not `fetch` calls in components.

---

## Error handling

- **Hub down, API up:** `useNotificationStream` reconnects on the same ramp as prices
  (0s/2s/5s/10s/30s, then 30s forever). While disconnected the badge is stale, not wrong —
  the next refetch or reconnect converges. No separate "reconnecting" UI for notifications;
  the price stream's existing indicator already tells the user the realtime layer is degraded.
- **Mark-read POST fails:** optimistic update rolls back on next refetch; the row simply
  stays unread. No toast, no retry queue — it is idempotent and self-healing.
- **Rule creation rejected (validation, per-user limit):** the API's ProblemDetails message is
  surfaced inline on the row form via the existing `Alert` primitive, mirroring the auth
  screens' pattern.
- **Broker down (the real one):** unchanged from 4a — the outbox holds `AlertTriggered`, the
  worker's supervision loop resubscribes, nothing is lost. This slice's chaos test is where
  that stops being a claim and becomes output from a test run.

---

## The chaos test

The automated version of 4a's never-rehearsed manual done-criteria, and the test the README's
headline claim has been waiting for. One test class in `MarketPulse.IntegrationTests`,
composing the **real processes** — the API via `WebApplicationFactory`, the Alerts worker via
its own `HostBuilder` — against Testcontainers SQL Server and RabbitMQ:

1. Register a user, create an `above` rule through the real API.
2. Publish a crossing tick into the broker; wait for the rule to reach `Triggered` in the
   database (the worker has evaluated, and the outbox row exists or is about to).
3. **Stop the RabbitMQ container** — the window between "rule marked Triggered" and "outbox
   dispatched" is the interesting one. The rule flip and the outbox row commit in one unit of
   work, so the row is observable the moment step 2 completes; the dispatcher interval is
   configured to a few seconds so the container stop (sub-second) reliably lands before the
   first dispatch attempt.
4. Assert, while the broker is down: the outbox row persists undispatched, no notification
   row exists, and neither process crashes.
5. **Restart the container.** The publisher's automatic recovery and the consumers'
   supervision loops resubscribe.
6. Assert exactly one notification row for the user — dispatched after the outage, deduped by
   the consumer's idempotency if redelivered. The SignalR push is not asserted here: the row
   is the guarantee, the push is the optimisation, and the browser's side of it is the
   Playwright journey's job.

Timing note: the test asserts on *database state transitions* with bounded polling, never on
sleeps; the outbox dispatcher interval is configured short via the worker's options. If the
broker container stop races past the dispatch on some run, the assertion "exactly one row,
zero loss" still holds — the test's claim is written to be true on either side of the race,
with the container stop placed to make the interesting window the common case.

---

## Testing

| Suite | What |
|---|---|
| Vitest (`apps/dashboard`) | `useNotificationStream` (parse, prepend-to-cache, reconnect dispatch — mirroring `usePriceStream.test.ts`); `useAlerts`/`useNotifications` query and mutation behaviour; badge derivation (unread count from cache states); watchlist-row alert control (create, validation error surfaced, re-arm, delete); panel mark-read-on-open |
| Vitest (`packages/ui`) | `Badge` (count rendering, accessible label, zero-hidden) |
| xUnit (unit) | Publisher dispose race: `DisposeAsync` serialises against an in-flight `PublishAsync` (the seam the fix creates makes this testable); consumer backoff floor after shutdown-triggered resubscribe |
| xUnit (integration) | The chaos test, as specified above |
| Playwright | `alerts.spec.ts`: register → add ticker to watchlist → create an `above` alert with threshold *below* the current displayed price (one-shot semantics make the next tick fire it deterministically — no waiting on the random walk to wander) → bell badge appears/increments → open panel → notification names the ticker and price → on reopen the row is read and the badge is gone |

### e2e harness change

The journey needs the full pipeline. RabbitMQ joins SQL Server as a docker-compose
prerequisite for the e2e suite, and the Alerts worker — which exposes no HTTP port for
Playwright's `webServer` to poll — is spawned from Playwright **global-setup** (`dotnet run
--project ../../src/MarketPulse.Alerts`) and killed in global-teardown. Readiness is inferred
the only way the worker exposes: the spec's first assertion polls observable behaviour, and
the worker's startup is given the same 120s budget as the API. If this proves flaky, the
fallback is noted in the plan: a trivial health endpoint on the worker — but it is not built
speculatively.

### Deliberately not tested

Browser-side broker-outage behaviour (the .NET chaos test owns the outage; the browser sees
only its consequences) · SignalR reconnect timing in Playwright (unit-tested policy, and
inherently timing-flaky in e2e) · concurrent multi-tab unread convergence (both tabs refetch;
eventual convergence is the design, not a timed assertion) · load/perf of the notifications
panel (50-row cap).

---

## Done criteria

1. All existing suites green; new Vitest, xUnit and Playwright tests green.
2. The Playwright journey passes: an alert set in the browser fires on a real tick through
   the real broker and appears in the panel with an unread badge, then reads as read.
3. The chaos test passes and its assertions demonstrate: zero notifications lost across a
   broker kill/restart, exactly one row delivered.
4. ADR-007 exists and the README no longer claims a state layer the codebase doesn't have;
   the corrected claim matches what ADR-007 argues.
5. The two 4a carryover fixes are in, each with the test that pins it.
6. `pnpm -r build`, `pnpm -r test`, `dotnet test` all pass from a clean checkout with
   `docker compose up -d` infrastructure.

---

## Interview category coverage

| # | Category | What this slice banks |
|---|---|---|
| 5 | **Frontend framework depth** ⭐ | Server-cache-first state architecture argued in an ADR; SignalR pushes patching TanStack Query caches; derived-not-stored unread state; optimistic mutations with rollback |
| 10 | **Architecture & system design** | ADR-007 as the worked example of *not* adding a layer — rejecting a documented technology because the data turned out to be server state; the README corrected to match reality |
| 11 | **Distributed systems & messaging** ⭐ | The chaos test — the difference between "I built an outbox" and "I can show you the test where the broker dies and nothing is lost" |
| 14 | **Testing** | A chaos test orchestrating two real hosts and two containers; deterministic e2e over a random-walk feed via one-shot trigger semantics; the harness decision to keep Docker control out of Playwright |

Category 11 was declared "not finished until 4b" by the 4a spec. This closes it, short of the
Polly resilience work slice 6 owns.

---

## What comes next

**Slice 5** (portfolio and transactions) is the roadmap's next stop — the largest remaining
gap between the README's product description and the running app, and where the deferred
idempotency keys on POST land. The follow-ons 4a named remain unscheduled: the in-memory rule
cache (wanted when a real feed replaces the random walk in slice 6) and notification channels
beyond the websocket, which is what "whether or not the app is open" ultimately requires.
