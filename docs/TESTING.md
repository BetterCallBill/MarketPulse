# Testing strategy

Trophy-shaped: heaviest at integration, thin but present at unit and end-to-end.

## What runs

| Level | Tooling | Scope |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants, `RandomWalk` bounds, `User` lockout, `RefreshToken` rotation, `AlertRule.Evaluate`'s truth table across both directions and boundary equality, its `MarkTriggered`/`Rearm` transitions and their illegal counterparts |
| Application unit | xUnit + NSubstitute | Handler orchestration with a substituted repository, password policy, `TickBroadcaster`'s sink fan-out (including one sink throwing), `RabbitMqTickSink`'s non-blocking/single-flight reconnect behaviour and its disposal of the channel it replaces, `RabbitMqConsumerService`'s resubscribe-on-channel-shutdown loop (including that a subscribe failure never escapes `ExecuteAsync`), `RabbitMqEventPublisher`'s channel replacement and disposed-state guard, `AlertEvaluator`'s recovery from one rule losing a concurrency race, `OutboxDispatcher`'s publish-then-mark-dispatched ordering against a substituted `IEventPublisher` |
| Architecture | xUnit + reflection | `Domain` references nothing outside the BCL; `MarketPulse.Application` references no messaging or web framework; `MarketPulse.Alerts` references no web framework; every CSRF-exempt hub declares no client-invokable methods |
| Backend integration | WebApplicationFactory + Testcontainers | Real SQL Server **and RabbitMQ**, real migration, real HTTP; auth journeys, CSRF, rate limiting, cross-user isolation — now extended to alert rules and notifications; the alerts pipeline end to end (rule creation → crossing tick → outbox row → dispatch → idempotent consumption → per-user SignalR delivery), redelivery producing no duplicate, a poison message reaching the dead-letter queue, an alert naming a non-existent user reaching it too rather than requeueing forever, and — through a loopback proxy that severs and restores the link to the broker — a recovering connection surviving and a consumer resubscribing after an outage; and the chaos test (`ChaosTests.cs`, below) |
| Frontend unit | Vitest | Stream reducer, zod schemas, `useNow`/`usePriceStream`/`PriceCell` hooks (stale-clock, initial-connect-failure), `useNotificationStream` (parse, cache prepend, reconnect dispatch — both hooks now share `features/realtime/reconnectPolicy`), `AlertCell` and `NotificationBell` behaviour, api-client CSRF and single-flight refresh |
| Frontend integration | Vitest + RTL + MSW | Watchlist screen render, 409-duplicate error path, login error paths, protected-route redirect |
| End-to-end | Playwright + Chromium | Four journeys through the real API and the production dashboard build, one of them (`alerts.spec.ts`) against the full alerts pipeline including a real Alerts worker and RabbitMQ |

**TDD in practice.** `AlertRule.Evaluate` is the concrete example behind the "TDD used for
the alert-evaluation engine" claim in the README's coverage map: its truth-table unit tests,
and each integration test across the alerts pipeline (`RabbitMqTopologyTests`,
`AlertEvaluationTests`, `OutboxDispatchTests`, `AlertPipelineTests`), were written and
confirmed failing — either against not-yet-existing types or a not-yet-existing project —
before the implementation that made them pass existed. `Evaluate` is a pure function of
`(rule, tick)` with no prior-price memory, which is what makes a truth table the right shape
of test for it in the first place.

## Chaos test

`ChaosTests.cs` (`MarketPulse.IntegrationTests`) is the automated version of 4a's
never-rehearsed manual done-criteria, and the test the README's headline claim — an alert
that survives a broker outage — was waiting for. One test class, composing the **real
processes**: the API via `WebApplicationFactory` (rule CRUD, the tick feed into the broker,
`AlertTriggeredConsumer` writing notification rows) and the Alerts worker's own hosted
services — `PriceConsumer` and `AlertEvaluator`, built exactly as `MarketPulse.Alerts`'s
`Program.cs` composes them — against Testcontainers SQL Server and RabbitMQ. `RabbitMqFixture`
binds the container to an OS-allocated free host port up front, rather than an
auto-assigned one: Docker Desktop does not reliably preserve an ephemeral host port across a
stop/start cycle on the same container, and a reassigned port would strand every client's
cached connection pointed at the old one — automatic recovery keeps retrying an address
nothing listens on anymore. Binding a fixed port keeps "same container, same address" true
across the outage this test causes.

`OutboxDispatcher` is deliberately **not hosted** in the worker built for this test. It is
driven by hand through the same public `DispatchPendingAsync` seam `OutboxDispatchTests`
uses. Hosting it would leave the test racing a background timer for the window it exists to
observe; calling it directly turns "rule marked Triggered, outbox row committed, nothing
dispatched yet" into a window the test stands still in rather than a few-hundred-millisecond
slot it might miss.

The sequence: create an `above` rule through the real API with a threshold the fake feed's
next tick is certain to cross; wait for the rule to reach `Triggered` in the database (the
rule flip and its outbox row commit in one unit of work, so `Triggered` means the row is
already there); **stop the RabbitMQ container**; attempt a dispatch and assert it confirms
nothing, the outbox row stays undispatched, no notification row exists, and `/health` still
answers `200` — the broker being down must not take the API down; **restart the container**;
retry dispatch until the row's own `DispatchedUtc` flips (not the dispatcher's return count,
which is shared, cross-class state within `MessagingCollection` — see below); and assert
exactly one notification row exists for the user, with a grace period to give a wrongly
duplicated delivery time to land and fail the assertion if it does.

## End-to-end

`tests/e2e` covers four journeys against a real API, a real SQL Server, and — since 4b —
a real broker and a real Alerts worker:

1. Register a new account, land on the protected watchlist, add a ticker, see its price
   tick from SignalR, sign out, and confirm `/` is genuinely closed afterwards.
2. Reload the page mid-session and stay signed in — the point of keeping the token in a
   cookie rather than in JavaScript memory.
3. Sign in with the wrong password and get an error without leaving the login page.
4. **(`alerts.spec.ts`, added in 4b)** Register, add a ticker, set an `above` alert at half
   the ticker's current displayed price — one-shot trigger semantics mean the very next
   tick fires it deterministically, with no need to wait on the random walk to wander —
   watch the rule flip to Triggered and the bell badge appear, open the panel and see the
   notification naming the ticker and price, and confirm the read state survives a full
   page reload because it lives on the server, not in the client.

Running it:

```bash
docker compose up -d
dotnet ef database update --project src/MarketPulse.Infrastructure
pnpm --filter @marketpulse/dashboard build   # Playwright serves the production build
pnpm e2e
```

The migration step is not optional and is the one thing that separates this level from
every other. The backend integration tests get their schema from Testcontainers via
`SqlServerFixture`, which migrates a throwaway instance itself; the E2E suite runs the real
API against a real SQL Server that nothing has prepared. Skipping it produces a confusing
failure rather than an obvious one: the API starts, `/health` answers 200 because it
reports liveness rather than readiness, Playwright concludes the server is up, and the
specs then fail on queries against a database that does not exist.

`docker compose up -d` now brings up RabbitMQ alongside SQL Server — a prerequisite the
alerts journey needs and the other three tolerate, since nothing in them touches the
broker. The Alerts worker itself exposes no HTTP port, so it cannot join Playwright's
`webServer[]` the way the API and the built dashboard do; it is instead spawned from
`global-setup.ts` (`dotnet run --project ../../src/MarketPulse.Alerts`, detached so
`global-teardown.ts` can kill its whole process tree) with no readiness probe of its own —
the alerts journey's first live assertion, a rule flipping to Triggered off a real tick, is
what actually proves the worker is up, on the same generous timeout the API gets.

Playwright starts both servers itself (`dotnet run` and `vite preview`) and waits on
`/health` — which exists precisely so there is an unauthenticated 200 to poll, since
`/api/v1/auth/me` answers 401 by design. It runs in CI as its own `e2e` job, gated behind
the backend and frontend jobs, and uploads a report artifact on failure.

## Deliberately not tested

- **No coverage threshold.** A threshold over a codebase this small drives noise, not
  quality. It arrives when there is a codebase to threshold.
- **No SignalR transport unit test.** That would test Microsoft's library. `PriceStreamTests`
  and `AlertPipelineTests` are the integration tests that prove each hub's own chain end to
  end — public broadcast on `PriceHub`, per-user delivery on `NotificationHub` — not the
  transport itself; the reducer tests cover our logic.
- **No load or brute-force simulation.** The rate limiter is tested for behaviour at its
  threshold — the tenth request passes, the eleventh is rejected — not under real
  concurrent load. Proving a fixed-window limiter holds up under contention needs a load
  harness, which is a different exercise.
- **No test that `Secure` cookies work over HTTPS.** The whole suite runs in Development,
  where `Secure` is off by design: .NET's `CookieContainer` refuses to send `Secure`
  cookies over plain HTTP, so turning it on would break every integration test rather than
  strengthen it. The attribute is instead covered by `AuthCookiesTests` as a pure function
  over the environment name.
- **No load test on evaluation throughput.** The random walk emits four ticks a second.
  Measuring throughput belongs to slice 12, against a feed and a rule count actually worth
  measuring.
- **No multi-instance worker test.** `AlertRule`'s `RowVersion` concurrency token — the
  mechanism that makes two worker instances a supported configuration rather than a
  double-notification bug — is exercised by forcing a concurrency conflict inside one test
  process (`Two_concurrent_evaluations_of_one_rule_produce_one_outbox_row`), not by running
  two worker processes against each other in CI.

## Shared-fixture determinism

`RabbitMqTopologyTests`, `AlertEvaluationTests`, `OutboxDispatchTests`, `AlertPipelineTests`,
`BrokerOutageTests` and `ChaosTests` all share one SQL Server container and one RabbitMQ
container across the whole `MessagingCollection`, never reset between test classes — the same
pattern the rest of the integration suite already uses for `SqlServerFixture`. Consequences
that shaped how these tests were written, not just what they assert:

- **Undispatched outbox rows and dead-lettered messages accumulate across classes within a
  run.** A test that asserts on queue *position* (`BasicGetAsync` returning exactly this
  message next) or a *raw count* of pending rows will pass in isolation and fail — or
  worse, silently pass for the wrong reason — once another test class has left rows or
  messages behind it. Two tests were hardened for exactly this: `OutboxDispatchTests`
  drains and searches for its own row by the `MessageId` it created rather than trusting
  `BasicGetAsync` to return it first, and `AlertPipelineTests`' dead-letter assertion does
  the same by a random correlation-id marker it mints itself.
- **The fix is always to be deterministic about the test's *own* data**, not about the
  shared resource's overall state — assert on a GUID or marker the test itself generated,
  drain-and-search rather than get-and-assert, and leave row/message *counts* as
  lower-bound (`>=`) assertions where the shared table is expected to carry residue from
  other classes in the same run.
- **`BrokerOutageTests` is why the outage is simulated with a proxy rather than by stopping
  the container.** Stopping the shared broker would take every other class's queues,
  exchanges and pending messages with it. A loopback `TcpProxy` that the test cuts and
  reconnects is indistinguishable from a broker restart to the client — an abruptly closed
  socket is an EOF, which the client reports as a library-initiated shutdown and then
  recovers from — while leaving the broker itself, and everyone else's state on it,
  untouched. The queue it consumes is a randomly named one it declares itself, for the same
  reason.
- **`ChaosTests` is the one test that does stop the shared container outright**, rather than
  simulate the outage with a proxy — the point of the chaos test is proving recovery from the
  real thing, not a stand-in for it. That is safe only because xUnit runs test classes within
  one collection sequentially, never in parallel with each other: nothing else in
  `MessagingCollection` is mid-flight against the broker while it is down. A `finally` block
  restarts the container even if an assertion above it has already failed, so the broker is
  back before the next class in the collection runs.
