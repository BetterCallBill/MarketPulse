# Slice 4a — Alerts pipeline, backend: "An alert that survives the queue"

**Date:** 2026-08-04
**Status:** Approved (design), pending implementation plan
**Previous slice:** [2026-08-03-dashboard-design-system-design.md](2026-08-03-dashboard-design-system-design.md)
**Parent spec:** [MarketPulse-Pro-README.md](../../MarketPulse-Pro-README.md)
**Roadmap entry:** [ROADMAP.md](../../ROADMAP.md) § 4a
**Category reference:** [intervew-aspects.md](../../intervew-aspects.md)

---

## Why this slice exists

The README's headline product claim is an alert that reaches the user "whether or not the app
is open", and its headline engineering claim is that alerts survive a broker outage. Neither
is substantiated today. Phase 3 is the one phase with no code at all: there is no broker, no
worker, no outbox, no alert rule, and no notification.

ADR-001 already committed the shape — a modular monolith plus one extracted `MarketPulse.Alerts`
worker — and `DependencyRuleTests` already forbids the Application layer from referencing
`RabbitMQ`, written in slice 1 against a broker that did not yet exist. This slice cashes both
in.

It is deliberately backend-only. The alerts UI, the notifications panel, unread state, the
Playwright journey and the chaos test are slice 4b. Splitting there keeps this slice near the
~8-task shape of slices 1–3, and it means 4b begins against a pipeline that is already proven
by integration tests rather than against one being built underneath it.

### Decisions taken, with rejected alternatives

| Decision | Chosen | Rejected because |
|---|---|---|
| Alert-rule data ownership | **Shared SQL Server.** The API owns rule CRUD; the worker reads the same database | Giving the worker its own schema, with rules replicated by `AlertRuleCreated/Updated/Deleted` events, is the textbook answer and roughly doubles the slice — a second `DbContext`, a second migration set, and a replication-lag failure mode to specify and test. What gets extracted here is the *compute*, which is what ADR-001 justified; the data stays shared and the ADR says so plainly rather than implying an autonomy the system does not have |
| Outbox placement | **On `AlertTriggered` only** | The README's diagram routes price ticks through the outbox. That is wrong and this slice corrects it: a tick is superseded a second later, so writing every one durably buys nothing and makes the queue path slower than the SignalR path it parallels. The genuine atomicity problem is elsewhere — "rule marked fired" and "notification event published" must not diverge — and that is exactly where the outbox goes |
| Broker client | **Raw `RabbitMQ.Client`** | MassTransit configures retry, DLQ and its own outbox for you, which answers "how do you handle poison messages?" with "the library does". Category 11 is staked on the topology, the ack policy and the dedupe being authored. The cost is roughly one extra task of connection and channel lifecycle code |
| Trigger semantics | **One-shot: `Active` → `Triggered`, re-armed explicitly** | Crossing detection ("fire only on a genuine transition") reads more naturally but requires the worker to hold last-price-per-ticker state, which is lost on restart and must be rebuilt — a failure mode 4b's chaos test would then have to account for. A cooldown window makes "did it fire?" time-dependent, which weakens both the unit tests and the zero-loss assertion. One-shot makes evaluation a pure function of `(rule, tick)` |
| Notification transport | **New `NotificationHub`, `Clients.User()`** | Reusing `PriceHub` saves a websocket per tab, which is a real cost. A separate hub is chosen so that "prices are public broadcast, notifications are private per-user" is a structural distinction rather than a convention — two data classifications, two authorization surfaces. Groups are not needed: SignalR's default `IUserIdProvider` reads `ClaimTypes.NameIdentifier`, the same claim `CurrentUser` reads |
| Concurrency control on rules | **`RowVersion` optimistic concurrency on `AlertRule`** | ADR-001 justifies the extraction by independent scaling, so two worker instances must be a supported configuration. They compete on one queue, so two ticks can race the same rule. Without a concurrency token the race double-notifies; a database-level lock would serialise every evaluation and forfeit the scaling the extraction was for |
| Idempotency keys on `POST /alerts` | **Deferred to slice 5** | The README promises them, but they are request-dedupe — a different concern from the messaging path, and one that belongs beside transaction idempotency where the money is |

---

## Scope boundary

### In scope

| Layer | Deliverable |
|---|---|
| Domain | `AlertRule` aggregate (`Evaluate`, `MarkTriggered`, `Rearm`, per-user limit, `RowVersion`); `Notification`; `OutboxMessage` |
| Application | `CreateAlertRuleCommand`, `DeleteAlertRuleCommand`, `RearmAlertRuleCommand`, `GetAlertRulesQuery`, `GetNotificationsQuery`, `MarkNotificationReadCommand`; `IAlertRuleRepository`, `INotificationRepository`, `IOutbox`, `IEventPublisher` abstractions; FluentValidation on rule creation |
| Infrastructure | `RabbitMqConnection` (lifecycle + automatic recovery), `RabbitMqTopology` (declarations), `RabbitMqEventPublisher` (publisher confirms), `RabbitMqTickSink`, message contracts, EF configuration + migration, repository implementations, `RabbitMqOptions` with `ValidateOnStart()` |
| Api | `AlertsController`, `NotificationsController`, `NotificationHub` at `/hubs/notifications`, `AlertTriggeredConsumer` hosted service, `TickBroadcaster` refactored to fan out over `ITickSink` |
| Alerts worker | New `src/MarketPulse.Alerts` .NET 10 Worker: `PriceConsumer`, the evaluation loop, `OutboxDispatcher` |
| Testing | Unit + integration as tabulated below, on Testcontainers SQL Server **and RabbitMQ** |
| Infra | `rabbitmq:3-management` added to `docker-compose.yml` with a healthcheck; `MarketPulse.Alerts` added to the solution |
| Docs | **ADR-009** (messaging architecture, with rejected alternatives); README diagram corrected; `TESTING.md` updated |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

All alerts and notifications UI · the RabbitMQ chaos test · unread badge state · email, SMS or
web-push delivery channels · percentage-change, volume, or moving-average rule types ·
compound conditions · alert history and audit · notification rate limiting or digesting ·
idempotency keys on rule creation · per-user quotas beyond a flat rule limit · scheduled or
market-hours-aware evaluation · replacing `FakeTickService` (slice 6).

---

## Architecture

### What does not change

Watchlists, authentication, refresh-token rotation, CSRF, rate limiting and the design system
are untouched. `PriceHub` keeps broadcasting prices to `Clients.All`. `FakeTickService` and
`RandomWalk` are unchanged — this slice adds a second consumer of the ticks they already
produce, not a new source.

### A channel is not a broadcast

The obvious way to get ticks onto the queue is a second `BackgroundService` reading
`PriceTickChannel`. It is wrong. `System.Threading.Channels` *distributes* items among
readers — each tick is delivered to exactly one of them — so a second reader would send
roughly half the ticks to SignalR and half to RabbitMQ, and the dashboard would appear to
tick at half speed with no error anywhere. The channel is declared `SingleReader = true`
today precisely because it has one.

So `TickBroadcaster` stays the single reader and gains a sink collection:

```
FakeTickService → PriceTickChannel → TickBroadcaster ─┬→ SignalRTickSink   → Clients.All
                                                      └→ RabbitMqTickSink  → marketpulse.prices
```

`ITickSink` has one method, `Task SendAsync(PriceTick, CancellationToken)`. `TickBroadcaster`'s
job becomes "read ticks, hand each to every sink", which is both smaller than what it does
today and the seam slice 7 will want when tick persistence arrives. A sink that throws is
logged and skipped — one failing sink must not stop the others, and it must not stop the
reader.

### Topology

| Exchange | Type | Queue | Binding | Message properties |
|---|---|---|---|---|
| `marketpulse.prices` | topic, durable | `alerts.prices` | `price.tick.#` | Non-persistent (delivery mode 1); queue `x-message-ttl 5000`, `x-max-length 1000`, `x-overflow drop-head` |
| `marketpulse.alerts` | topic, durable | `api.notifications` | `alert.triggered` | Persistent (delivery mode 2), publisher confirms; `x-dead-letter-exchange marketpulse.alerts.dlx` |
| `marketpulse.alerts.dlx` | fanout, durable | `api.notifications.dlq` | — | Terminal; drained by hand |

The two halves are treated deliberately differently, and the asymmetry is the point. Prices
are *lossy by design*: the TTL and max-length exist so that a worker which was down for a
minute wakes up and evaluates current prices rather than grinding through a stale backlog,
and the routing key carries the ticker (`price.tick.IVV`) so a future worker can bind to a
subset. Alert events are the opposite — durable, confirmed, dead-lettered — because a lost one
is a promise broken to a user.

Both applications declare the full topology at startup. Declarations are idempotent in AMQP,
so whichever process starts first wins and the other is a no-op; neither has to depend on the
other having run.

### Evaluation

The worker consumes `alerts.prices` with `prefetch = 100` and manual acks. For each tick it
loads that ticker's `Active` rules and evaluates:

```csharp
public bool Evaluate(decimal price) =>
    Status == AlertRuleStatus.Active &&
    (Direction is AlertDirection.Above ? price >= Threshold : price <= Threshold);
```

A pure function of `(rule, tick)` with no prior-price memory — which is what makes the
truth-table unit tests worth writing, and what makes the README's "TDD used for the
alert-evaluation engine" claim honest rather than decorative.

On a match, one transaction does two things:

```
BEGIN
  UPDATE AlertRules SET Status='Triggered', TriggeredUtc=…, TriggeredPrice=…
    WHERE Id=… AND RowVersion=…          -- loser of a race gets 0 rows
  INSERT INTO OutboxMessages (Id, Type, Payload, OccurredUtc)
COMMIT
```

A `DbUpdateConcurrencyException` means another worker instance triggered the same rule first.
That is expected under horizontal scaling, not an error: it is logged at debug, the tick is
acked, and nothing is published. This is the mechanism that makes ADR-001's "scales
independently" true rather than aspirational.

The tick is acked after the transaction commits. If the process dies between the two, the tick
is redelivered and the rule is no longer `Active`, so nothing happens twice.

### Outbox dispatch

`OutboxDispatcher` runs in the worker, polling every 500ms for rows where `DispatchedUtc IS
NULL`, ordered by `OccurredUtc`, in batches. Each is published to `marketpulse.alerts` and
marked dispatched **only after the broker confirms it**. A publish that fails or times out
leaves the row untouched and increments `AttemptCount`; the next pass retries it.

This is at-least-once, on purpose. A confirm that is lost on the way back republishes a
message the broker already has, which is why the consumer dedupes.

Polling rather than listening is chosen for its failure behaviour: a dispatcher that missed a
notification signal would leave the row stranded until something else woke it, whereas a poll
loop is self-healing by construction. 500ms of latency on a path that is already eventually
consistent is not worth engineering away.

### Consumption and delivery

`AlertTriggeredConsumer` is a hosted service in the API. Per message:

1. Insert a `Notification`. `MessageId` carries a unique index, so a redelivery raises a
   duplicate-key violation, which is caught and treated as success — the dedupe is enforced by
   the database, not by a read-then-write that races itself.
2. Push to `Clients.User(userId)` on `NotificationHub`.
3. Ack.

If SignalR delivery fails or the user is offline, the row is already persisted and 4b's panel
will show it on next load. Real-time is the optimisation; the row is the guarantee.

Any other failure is classified before it is acknowledged, because "retry" and "give up" are
opposite answers to opposite problems:

| Failure | Action |
|---|---|
| Duplicate `MessageId` | Ack. Already delivered; this is success, not an error |
| Transient — database unreachable, connection reset | Nack **with** requeue. The alert is real and the fault is ours; dead-lettering it would lose exactly what the outbox exists to protect |
| Anything else — malformed payload, unknown message type, a `UserId` that does not exist | Nack **without** requeue → DLX → `api.notifications.dlq` |

Requeue-on-transient can loop if the database stays down, which is deliberate: the message
keeps its place until the database returns. What it must never do is dead-letter a valid
alert because SQL Server was restarting. Distinguishing a slow-burning transient fault from a
permanent one — a redelivery counter, or a delayed retry queue — is a real gap and belongs
with the observability slice, where there is somewhere to see it happening.

### Correlation

`CorrelationIdMiddleware` already stamps every request, and alert-rule CRUD inherits that for
free. The alert *delivery* path does not: it begins with a tick, not a request, so there is no
ambient id to propagate.

So the tick sink mints one per tick, and it is then carried the length of the chain — into the
worker's log scope on consume, onto the `OutboxMessage` row, onto the AMQP `correlation_id`
property when the dispatcher publishes, and into the API consumer's log scope. One searchable
value spans tick → worker → queue → API → user. Worth stating precisely, because "correlation
IDs propagate from browser → API → queue → worker" (README, line 282) describes a
request-originated flow, and this one is not: the browser is the destination here, not the
source. The observability slice inherits the mechanism either way.

### Domain model

**`AlertRule`** — `Id`, `UserId`, `Ticker`, `Direction` (`Above` | `Below`), `Threshold`,
`Status` (`Active` | `Triggered`), `CreatedUtc`, `TriggeredUtc?`, `TriggeredPrice?`,
`RowVersion`. Two states only: a "paused" state has no way to be reached from the API this
slice ships, and deleting a rule is one request. The aggregate enforces what it can see —
threshold strictly positive, and the legality of each status transition.

The 20-rule-per-user limit (`AlertRule.MaxPerUser`, mirroring `Watchlist.MaxItems` and for the
same reason) is enforced in the create handler against a count query, and the unknown-ticker
check in a FluentValidation rule against reference data, exactly as `AddWatchlistItemValidator`
does. Neither can live on the aggregate: unlike `Watchlist`, which owns its items and can
therefore count them, an `AlertRule` is a standalone row that can see neither its siblings nor
the ticker table. Worth stating because it is a real difference in aggregate design between two
features that otherwise look alike.

**`Notification`** — `Id`, `UserId`, `MessageId` (unique), `AlertRuleId`, `Ticker`,
`Direction`, `Threshold`, `TriggeredPrice`, `OccurredUtc`, `IsRead`, `CreatedUtc`. The rule is
snapshotted onto the notification rather than joined, so deleting a rule does not rewrite
history.

**`OutboxMessage`** — `Id` (which *is* the `MessageId`), `Type`, `Payload` (JSON),
`CorrelationId`, `OccurredUtc`, `DispatchedUtc?`, `AttemptCount`. Filtered index on
`DispatchedUtc IS NULL`: the dispatcher's query must not scan a table that only grows.

### Message contracts

Contracts live in `Infrastructure/Messaging/Contracts` rather than a shared package. A
contracts assembly is what you add when the worker owns its own database and the two can
version independently; it does not, and pretending otherwise would be ceremony. ADR-009
records this as the first thing to change if the worker is ever given its own store.

```csharp
record PriceTickMessage(string Ticker, decimal Price, DateTimeOffset TimestampUtc);

record AlertTriggeredMessage(
    Guid MessageId, Guid AlertRuleId, Guid UserId, string Ticker,
    string Direction, decimal Threshold, decimal TriggeredPrice,
    DateTimeOffset OccurredUtc);
```

### Layering

The worker references Infrastructure, and through it Application and Domain. It references
`Microsoft.AspNetCore` not at all — `DependencyRuleTests` gains an assertion to that effect,
which is the mirror image of the rule the Application layer already lives under. `IOutbox` and
`IEventPublisher` are declared in Application and implemented in Infrastructure, so the
existing "Application references no messaging framework" test keeps passing without amendment.

### API surface

| Endpoint | Behaviour |
|---|---|
| `POST /api/v1/alerts` | Creates a rule for the current user; `201` with the rule |
| `GET /api/v1/alerts` | The current user's rules |
| `DELETE /api/v1/alerts/{id}` | Deletes; `204` |
| `POST /api/v1/alerts/{id}/rearm` | `Triggered` → `Active`. One-shot semantics need a way back, or a fired alert is single-use junk |
| `GET /api/v1/notifications` | The current user's notifications, newest first. `?take=` (default 50, max 100) and `?skip=` — enough for 4b's panel, and not a paging abstraction built before anything pages |
| `POST /api/v1/notifications/{id}/read` | Marks read; `204` |

All `[Authorize]`, all scoped by `ICurrentUser`, all under the existing CSRF middleware.

---

## Error handling

| Failure | Response |
|---|---|
| Unknown ticker | `400`, `/errors/unknown-ticker` |
| Threshold ≤ 0, or missing direction | `400` with per-field errors |
| 20-rule limit reached | `409`, `/errors/alert-limit-reached` |
| Duplicate active rule (same ticker, direction and threshold) | `409`, `/errors/duplicate-alert-rule` |
| Rule or notification belongs to another user | **`404`**, not `403` — an existence check is an information leak, and slice 2 set this posture |
| Re-arming a rule that is already `Active` | `409`, `/errors/alert-not-triggered` |

All RFC 7807 ProblemDetails carrying the correlation ID, per slice 1.

### Broker unavailability

The broker being down must not take the API down with it. Concretely:

- Both applications retry the initial connection with exponential backoff and jitter rather
  than crash-looping, and use `AutomaticRecoveryEnabled` for reconnection thereafter.
- `RabbitMqTickSink` failures are logged and dropped. Ticks are lossy by design and SignalR
  delivery must not degrade because the broker is unwell.
- Alert-rule CRUD keeps working — it is a database operation and touches no broker.
- Triggered alerts accumulate as undispatched outbox rows and flush when the broker returns.
  **This is the mechanism behind the README's zero-lost-alerts claim, and 4b's chaos test is
  what proves it.** This slice builds it; it does not yet claim it.
- `/health` stays a liveness check and does not go unhealthy on broker loss. A readiness
  endpoint that distinguishes the two belongs to the observability slice.

---

## Testing

| Level | Tooling | Covers |
|---|---|---|
| Unit | xUnit | `Evaluate` truth table across both directions, boundary equality, and every non-`Active` status; `MarkTriggered` / `Rearm` transitions and their illegal counterparts; the 20-rule limit; outbox payload round-trip; `TickBroadcaster` fan-out including one sink throwing |
| Integration | WebApplicationFactory + Testcontainers (SQL Server **and RabbitMQ**) | The full path: create a rule via the API → publish a crossing tick → assert the outbox row → assert it is dispatched → assert exactly one `Notification` → assert a connected SignalR client receives it. Redelivering the same `MessageId` still yields one row. A poison message reaches the DLQ rather than blocking the queue. A rule that does not cross produces nothing. Concurrent triggers of one rule produce one notification. **User A cannot read, delete or re-arm user B's rules, and cannot see B's notifications** |
| Worker | xUnit | `OutboxDispatcher` does not mark a row dispatched when the publish is unconfirmed |

`Testcontainers.RabbitMq` is added; the backend CI job needs no workflow change, since
Testcontainers starts its own containers and the runner already has Docker.

### Deliberately not tested

- **No chaos test.** Killing the broker mid-flow is slice 4b, and it is the test that turns
  "the outbox exists" into "no alert was lost".
- **No load test on evaluation throughput.** The random walk emits four ticks a second.
  Measurement belongs to slice 12, against something worth measuring.
- **No SignalR transport test** — unchanged from slices 1 and 2. That tests Microsoft's library.
- **No multi-instance worker test.** The concurrency token is unit-tested through a forced
  `RowVersion` mismatch, not by running two workers in CI.

---

## Known limitations, stated rather than glossed

**Rules are queried per tick.** At four ticks a second with an index on `(Ticker, Status)`
this is four indexed reads a second, which is nothing. It is also the first thing that breaks
under a real feed, and the named remedy is an in-memory rule cache invalidated on write.
Recorded here so it is a deferred decision rather than an oversight — and kept out of slice 7,
which carries the deliberate N+1 postmortem and does not need a second performance story
competing with it.

**A rule can fire the instant it is created.** Creating "above $50" while the price is already
$55 triggers on the next tick. This is the honest consequence of stateless evaluation, and
arguably correct — the user asked to be told when it is above $50, and it is. The alternative
requires the crossing-detection state that was rejected above.

**The worker shares the API's database.** It reads rules and writes rule state and outbox rows
directly. This is coupling, and a schema change to `AlertRules` is a coordinated deploy of two
services. Accepted because what the extraction is for is independent scaling of evaluation,
not independent deployment of a data model. ADR-009 must say this in those words rather than
implying autonomy the system does not have.

**Notification delivery is at-least-once end to end, deduped at one point.** The database's
unique index on `MessageId` is the only thing preventing duplicates. If that index were
dropped, redelivery would silently double-notify and no test outside the integration suite
would catch it.

---

## Done criteria

Slice 4a is complete when, from a clean clone:

- [ ] `docker compose up` starts SQL Server and RabbitMQ, both healthy
- [ ] The API and `dotnet run --project src/MarketPulse.Alerts` both start and connect
- [ ] Creating an alert via the API and waiting for the random walk to cross it produces a
      `Notification` row without any manual step
- [ ] A SignalR client connected to `/hubs/notifications` as that user receives it; a client
      connected as a different user does not
- [ ] Stopping the broker leaves alert-rule CRUD working and the dashboard still ticking
- [ ] Restarting the broker flushes the accumulated outbox rows
- [ ] Replaying an `AlertTriggered` message creates no second notification
- [ ] A malformed message lands in `api.notifications.dlq` and the queue keeps moving
- [ ] `dotnet test` and CI are green
- [ ] ADR-009 is committed with a rejected-alternatives section
- [ ] The README's architecture diagram no longer routes price ticks through the outbox
- [ ] `ROADMAP.md`'s status table and slice list are updated in the finishing commit

---

## Interview category coverage

| # | Category | Slice 4a banks | Still owed |
|---|---|---|---|
| 4 | **ASP.NET Core depth** | Three more hosted services with real lifecycle and failure handling; a second options type with `ValidateOnStart()`; a Worker Service host beside the web host | Minimal APIs beside controllers; action filters; ADR-005 |
| 7 | **Web API design** | Two more resource-oriented controllers; ProblemDetails on six new failure modes; `404`-over-`403` applied consistently | OIDC; idempotency keys; documented versioning strategy |
| 8 | **Data access** | Optimistic concurrency with `RowVersion`; a filtered index chosen for a specific query; transactional write of state and event together | Dapper; execution plans; the N+1 postmortem |
| 10 | **Architecture** ⭐ | ADR-001's extraction actually performed; the dependency rule extended to a second deployable | Micro-frontends; ADR-004 |
| 11 | **Distributed systems & messaging** ⭐ | Topic exchange and DLQ authored by hand; outbox with publisher confirms; idempotent consumer deduped at the database; eventual consistency documented end to end; correlation across the broker | Polly retry and circuit breaker (slice 6); **the chaos test (4b)** |
| 14 | **Testing** | TDD on the evaluation engine; Testcontainers RabbitMQ beside SQL Server; cross-user isolation extended to two new resources | The chaos test; coverage philosophy in practice |

Category 11 moves from nothing to substantially banked, which is the point of this slice. It
is not finished until 4b: a chaos test is what separates having built an outbox from having
proved it works.

---

## What comes next

**Slice 4b** takes this pipeline and makes it visible and provable: the alerts UI and
notifications panel on `packages/ui`, unread state, the Playwright journey, and the chaos test.
The client-state question the roadmap flags — whether Zustand ever appears, or ADR-007 gets
rewritten — is likely decided there, by whether unread handling turns out to be genuinely
client state or just server cache in disguise.

Two follow-ons this slice names but does not schedule: an **in-memory rule cache**, wanted
when a real feed replaces the random walk in slice 6, and **notification channels beyond the
websocket** — email or web push — which is what the README's "whether or not the app is open"
finally requires. A websocket only reaches a user who has the app open.
