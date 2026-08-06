# ADR-009: Messaging architecture for the alerts pipeline

**Status:** Accepted · **Date:** 2026-08-04

## Context

Phase 3 needed a way to evaluate price alerts and deliver notifications without either
blocking the price-tick path that already serves the dashboard, or losing an alert to a
broker outage. ADR-001 already committed the shape — a modular monolith plus one extracted
`MarketPulse.Alerts` worker, talking to RabbitMQ — but left the messaging design itself
unspecified: how the worker gets the data it evaluates against, where the transactional
outbox sits, which broker client to use, what "triggered" means, how a notification reaches
exactly the right user, and how two worker instances avoid double-notifying. This ADR
records those six decisions and the alternatives rejected for each.

## Decision

1. **Shared database, extracted compute.** The `MarketPulse.Alerts` worker reads and writes
   the API's SQL Server directly — the same `AlertRules` table, the same `OutboxMessages`
   table, no second schema and no replication. What ADR-001 justified by extracting the
   worker is independent *scaling* of alert evaluation, not independent *deployment* of a
   data model. A schema change to `AlertRules` is a coordinated deploy of two services; the
   ADR says so plainly rather than implying an autonomy the system does not have.

2. **The outbox sits on `AlertTriggered`, not on price ticks.** The README's original
   architecture diagram routed price ticks through the outbox. That was wrong, and this
   slice corrects both the diagram and the design it described: a tick is superseded a
   second later, so writing every one durably buys nothing and only slows the queue path
   down relative to the SignalR path it parallels. The genuine atomicity problem is
   elsewhere — "rule marked fired" and "notification event published" must not diverge —
   and that is exactly where the outbox goes: one transaction updates `AlertRule.Status` and
   inserts the `OutboxMessage` row together.

3. **Raw `RabbitMQ.Client` over MassTransit.** MassTransit would configure retry, DLQ, and
   its own outbox for you, which answers "how do you handle poison messages?" with "the
   library does." The topology, the ack policy, and the dedupe are the substance this slice
   exists to demonstrate, and a library would have configured all three away. The cost is
   authoring connection and channel lifecycle code by hand.

4. **One-shot trigger semantics.** An `AlertRule` moves `Active` → `Triggered` on the first
   matching tick and stays there until explicitly re-armed via
   `POST /alerts/{id}/rearm`. Evaluation is a pure function of `(rule, tick)`, with no
   memory of the previous price.

5. **A separate `NotificationHub` at `/hubs/notifications`**, distinct from `PriceHub`.
   Prices are public broadcast (`Clients.All`); notifications are private per-user
   (`Clients.User(userId)`). These are two different data classifications, and keeping them
   on two hubs makes that a structural distinction rather than a convention enforced by
   review. The cost is a second websocket per browser tab.

6. **`RowVersion` optimistic concurrency on `AlertRule`.** ADR-001 justifies the extraction
   by independent scaling, so two worker instances competing on one queue must be a
   supported configuration, not an assumption. Without a concurrency token, two workers
   racing the same rule on two ticks would both "win" and double-notify.

## Rationale

**Shared database vs. worker-owned schema.** Giving the worker its own schema, with rules
replicated by `AlertRuleCreated/Updated/Deleted` events, is the textbook answer for service
autonomy and roughly doubles the slice's size — a second `DbContext`, a second migration
set, and a replication-lag failure mode that would need its own design and its own tests.
What is being extracted here is compute, not a bounded context with its own data; sharing
the database is the honest reflection of that.

**Outbox placement.** A tick is lossy by design elsewhere in this same architecture (topic
exchange with a 5-second TTL, `x-max-length` 1000, `x-overflow drop-head`) precisely because
a stale tick is worthless — a worker that was down for a minute should evaluate current
prices, not grind through a backlog. Durably queuing every tick through an outbox would
contradict that lossiness while adding write cost to the hottest path in the system for no
correctness gain. A triggered alert is the opposite: it happens once, must not be dropped,
and losing one is a promise broken to a user. The outbox belongs where the loss would
actually matter.

**Raw client vs. MassTransit.** The interview category this slice banks against — category
11, distributed systems and messaging — is staked on the topology, ack policy, and dedupe
being authored by hand, not configured through a library's defaults. Choosing MassTransit
would have made this a demonstration of using a library rather than of the underlying
mechanics.

**One-shot vs. crossing detection or a cooldown window.** Crossing detection — "fire only on
a genuine transition from below threshold to above it" — reads more naturally, but requires
the worker to hold last-price-per-ticker state. That state is lost on every restart and must
be rebuilt, which is a real failure mode the chaos test (slice 4b) would then have to
account for. A cooldown window makes "did it fire?" a function of wall-clock time, which
weakens both the unit tests (the truth table stops being pure) and the zero-loss assertion
the outbox exists to prove. One-shot evaluation needs none of that: `Evaluate(rule, tick)` is
a pure function, which is what makes the truth-table unit tests worth writing and the
README's "TDD used for the alert-evaluation engine" claim honest.

**A separate hub vs. reusing `PriceHub`.** Reusing `PriceHub` and gating delivery with
SignalR groups would save the extra websocket. It was rejected because groups make the
public/private distinction a matter of correct group membership rather than a fact about
which hub a message travels over — one misplaced `Clients.Group(...)` call away from a
broadcast leak. `Clients.User()` on a dedicated hub makes the class of bug structurally
harder to write. No custom `IUserIdProvider` was needed: SignalR's default reads
`ClaimTypes.NameIdentifier`, the same claim `CurrentUser` already reads for every other
authorization decision in the API.

**`RowVersion` vs. a database-level lock.** A pessimistic lock (e.g. `SELECT ... WITH
(UPDLOCK)`) would serialize every evaluation across worker instances and forfeit the
scaling the extraction was performed for in the first place. `RowVersion` optimistic
concurrency lets both instances race freely and only pays a cost — a caught
`DbUpdateConcurrencyException`, logged at debug, no publish — on the rare tick that two
instances evaluate at once.

## Rejected alternatives

- **Worker-owned schema fed by replication events** (data ownership). Rejected: doubles the
  slice's scope for autonomy this system does not need at its current size, and introduces a
  replication-lag failure mode that would need its own design and tests. See decision 1.
- **Outbox on every price tick** (outbox placement). Rejected: a tick is superseded a second
  later, so durable writes on that path buy nothing and slow down the SignalR path it
  parallels. See decision 2.
- **MassTransit** (broker client). Rejected: it would configure the exact mechanics — retry,
  DLQ, outbox — that this slice exists to author and demonstrate by hand. See decision 3.
- **Crossing detection** (trigger semantics). Rejected: requires restart-fragile
  last-price-per-ticker state that the chaos test would then have to account for. See
  decision 4.
- **A cooldown window** (trigger semantics). Rejected: makes "did it fire?" time-dependent,
  weakening the truth-table unit tests and the zero-loss assertion. See decision 4.
- **Reusing `PriceHub` with SignalR groups** (notification transport). Rejected: saves one
  websocket per tab, but makes the public/private distinction a matter of correct group
  membership rather than a structural fact about which hub carries the message. See
  decision 5.
- **A database-level lock on `AlertRule`** (concurrency control). Rejected: serializes
  evaluation across worker instances and forfeits the independent scaling ADR-001 justified
  the extraction by. See decision 6.

## Consequences

**A rule can fire the instant it is created.** Creating "above $50" while the price is
already $55 triggers on the next tick. This is the honest consequence of stateless,
one-shot evaluation, and arguably correct — the user asked to be told when it is above $50,
and it is. The alternative requires the crossing-detection state that was rejected above.

**Rules are queried per tick.** At four ticks a second with an index on `(Ticker, Status)`
this is four indexed reads a second, which costs nothing today. It is the first thing that
breaks under a real feed; the named remedy is an in-memory rule cache invalidated on write,
deferred rather than built now.

**Notification delivery is at-least-once end to end, deduped at exactly one point.** The
database's unique index on `Notification.MessageId` is the only thing preventing duplicate
notifications from a redelivered `AlertTriggered` message. If that index were dropped,
redelivery would silently double-notify and no test outside the integration suite would
catch it.

**A single blocking sink can freeze SignalR delivery for every client.** `TickBroadcaster`
awaits its sinks sequentially on `PriceTickChannel`'s single reader — that is the correct
shape for a channel declared `SingleReader = true`, but it means any one sink that blocks
blocks every sink behind it. `RabbitMqTickSink` was originally written to await connection
establishment inline before publishing. Because `RabbitMqConnection` retries an unreachable
broker with unbounded exponential backoff, the first tick to hit a down broker would have
wedged the reader indefinitely, freezing SignalR delivery to every connected client for as
long as the broker stayed down — precisely the degradation the spec's own "SignalR delivery
must not degrade because the broker is unwell" rules out. The fix was to make the sink
non-blocking: it reads its cached channel, publishes immediately if one is open, and
otherwise drops the tick and triggers a single-flight background reconnect without waiting
on it. Ticks are already lossy by design, so dropping one costs nothing a subscriber would
notice; blocking every subscriber on one broker connection would have. The general lesson —
a shared single-reader fan-out is only as fast as its slowest sink, so every sink on it must
be non-blocking by construction, not just well-behaved in the common case — generalizes
beyond RabbitMQ to any future sink added to the same broadcaster.

**A push failure must not dead-letter an already-committed row.** The consumer's original
failure classification put a SignalR push failure through the same catch-all as a malformed
message or a database error, which nacked without requeue and sent it to the DLQ — even
though the `Notification` row was already committed by that point. This contradicted the
spec's own stated guarantee, "the row is the guarantee; real-time is the optimisation": a
push failure is not a delivery failure, it is the loss of an optimisation on top of a
delivery that already succeeded. The fix gives the push its own inner catch, scoped to only
the `Clients.User(...).SendAsync(...)` call: on any exception there, it logs and still acks.
The message's outer failure classification (duplicate → ack, transient → requeue, anything
else → dead-letter) is now explicitly about persistence, not about delivery on top of it.

**`/hubs/notifications` is exempt from CSRF, and that exemption is a pattern, not a
one-off.** SignalR's negotiate handshake is a POST, and neither `PriceHub` nor
`NotificationHub` has a client-invokable method for a forged cross-site request to trigger —
both are empty `Hub` classes that only ever push server-to-client. The exemption's risk is
also bounded independently of that fact: `mp_access` is `SameSite=Lax`, and CORS is
credentialed against an explicit origin allowlist, so an exempt path is not simply open to
the internet. `CsrfMiddleware.ExemptHubs`
is the single source of truth for which hub sits behind which exempt path, and
`CsrfMiddlewareTests` asserts by reflection that every hub named there declares no public,
client-invokable methods (`DeclaredOnly`, excluding what it inherits from `Hub`) — so the
safety of the exemption is enforced, not just documented. The rule this slice establishes is
general: any push-only hub with no client-invokable methods may join the exempt list, on the
same reasoning, guarded by the same test.

**Automatic recovery reconnects a connection; it does not keep a consumer alive.** The
biggest lesson of this slice, and one that cost a silent total failure of the delivery path
to learn. `AutomaticRecoveryEnabled` invites you to treat reconnection as solved, and two
separate pieces of code took that invitation. `RabbitMqConnection` treated
`IsOpen == false` as "this connection is finished" and disposed it before opening a
replacement — but `AutorecoveringConnection.IsOpen` delegates to the *inner* connection, so
it reads false for the whole recovery window, and the disposal destroyed a connection that
was about to come back along with every channel and consumer on it. Both `BackgroundService`
consumers, meanwhile, subscribed once at startup and then parked on
`Task.Delay(Timeout.Infinite)`, which made their liveness entirely a property of somebody
else's connection object. The combination meant a single broker blip left the API consuming
no alerts and the worker evaluating no ticks, permanently, with nothing thrown, nothing
logged, and `/health` still answering `ok` — the worst shape a failure can take. Two changes,
because they defend against different things. The connection now decides "recovering" versus
"finished" with the same predicate the client uses internally
(`ShouldTriggerConnectionRecovery`: peer-initiated unless access was refused,
library-initiated unless the AppDomain is unloading, otherwise terminal) rather than by
guessing from `IsOpen`. And both consumers now run on a shared `RabbitMqConsumerService`
supervision loop that owns its own subscription: it resubscribes when its channel shuts
down, disposes the spent channel first so that the client's topology recovery cannot leave a
second copy of the consumer behind, backs off exponentially while the broker is unreachable,
and catches everything so no exception can escape into
`BackgroundServiceExceptionBehavior.StopHost`. The general rule this establishes: a library's
recovery feature is a convenience on the happy path, never the thing a liveness guarantee is
allowed to rest on. Anything that must keep running has to be able to rebuild itself.

**A poison message is defined by its fault, not by its exception type.** The consumer's
first cut classified persistence failures by exception type: `DbUpdateException` meant
"transient, requeue" unless it was a duplicate-key violation. That is the wrong axis.
`Notifications.UserId` carries a foreign key, so an alert naming a user who does not exist
raises SQL 547 — a `DbUpdateException` like any other — and requeued at SQL-round-trip
speed forever, hot-spinning the database, growing the log without bound, and never reaching
the dead-letter queue the design put it on. Classification is now by SQL Server error
number, an allow-list of faults that are permanent properties of the message (547, 515, 245,
2628, 8114, 8152) with everything else falling through to requeue. The allow-list direction
is deliberate: requeueing a permanent fault wastes cycles, whereas dead-lettering a
transient one loses a user's alert, so the unknown case has to default to the recoverable
side.

**Losing an optimistic-concurrency race costs one rule, not one tick.** `RowVersion` was
chosen so that two worker instances could race freely (decision 6), but the loser's recovery
matters as much as the mechanism. One tick can cross several rules — "IVV above 100" and
"IVV above 105" are both crossed at 106 — and `PriceConsumer` acks the tick whether or not
every rule was evaluated, so a rule skipped because a sibling lost a race never sees that
price again. Recovering properly is more than continuing the loop: a failed `SaveChanges`
rolls back its transaction but leaves everything it attempted still tracked, so the
conflicted rule and the `OutboxMessage` enqueued beside it have to be dropped from the unit
of work explicitly, or the next rule's save replays both and publishes an alert this
instance never won. This is what `IOutbox.Discard` exists for, and it is the one place the
outbox pattern's "the caller's single `SaveChanges` is what makes it atomic" contract needs
a way to say "not that one, after all."

**The requeue loop is bounded, as of the observability slice.** A transient failure
(database unreachable, connection reset) on the consumer side used to be nacked with
requeue — deliberately, so a redelivered alert was never lost to a database blip, but with
no limit: if the database stayed down, the same message could loop between the consumer and
the queue indefinitely. `TransientRetry` replaces the bare requeue with a republish to the
same queue carrying an incremented `x-retry-count` header, acking the original only once
the republish has succeeded; a plain nack+requeue could not have counted anything, because a
requeued message comes back with identical headers. Past `RabbitMqOptions.RetryLimit`
(default 5) the message is nacked without requeue instead, landing on the dead-letter queue
the topology already provides. Both paths are now visible —
`marketpulse.notifications.redeliveries` and `marketpulse.notifications.dead_letters` —
which is what the observability slice was for. A delayed retry queue (redelivering after a
backoff rather than immediately) was the heavier alternative and is rejected for the same
reason MassTransit was in decision 3: it is infrastructure this slice would rather author
plainly than configure. The accepted trade is that retries stay hot — at broker speed, no
backoff — until the cap; five fast, free redeliveries costs less than the delay would, and
the cap is what turns "forever" into "at most `RetryLimit` attempts."

"Acking the original only once the republish has succeeded" is only actually true because
`AlertTriggeredConsumer` now opts its channel into publisher confirmations
(`RabbitMqConsumerService.RequiresPublisherConfirms`), which every other consumer leaves off.
Without confirms, `BasicPublishAsync` returns once the message is written to the socket, not
once the broker has accepted it, and `mandatory: true` alone reports an unroutable message
only through an async `BasicReturnAsync` event nothing here was subscribed to — an awaited
republish could "succeed," the original get acked, and the copy never actually land, which is
the exact loss this task exists to prevent. With confirmation tracking on, the client
correlates the broker's response to the specific publish by sequence number and throws
(`PublishException`, or `PublishReturnException` for a `basic.return`) if it was nacked or
unroutable, so `TransientRetry`'s catch — leave the original unacked, let broker redelivery
retry — is reachable the way the code already assumed. `PriceConsumer`'s channel is
unaffected: confirmations only change publish behaviour, and it never publishes.
