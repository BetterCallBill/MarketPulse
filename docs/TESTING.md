# Testing strategy

Trophy-shaped: heaviest at integration, thin but present at unit and end-to-end.

## What runs

| Level | Tooling | Scope |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants, `RandomWalk` bounds, `User` lockout, `RefreshToken` rotation, `AlertRule.Evaluate`'s truth table across both directions and boundary equality, its `MarkTriggered`/`Rearm` transitions and their illegal counterparts, `Portfolio`'s average-cost re-averaging (a first buy, a second buy re-averaging, a sell realising P&L against the average while leaving it unchanged, selling to zero retaining history so a rebuy resets the basis), oversell and unknown-holding rejection, fractional-unit precision, and the `Version` counter incrementing on every accepted trade and staying put on a rejected one |
| Application unit | xUnit + NSubstitute | Handler orchestration with a substituted repository, password policy, `TickBroadcaster`'s sink fan-out (including one sink throwing), `RabbitMqTickSink`'s non-blocking/single-flight reconnect behaviour and its disposal of the channel it replaces, `RabbitMqConsumerService`'s resubscribe-on-channel-shutdown loop (including that a subscribe failure never escapes `ExecuteAsync`), `RabbitMqEventPublisher`'s channel replacement and disposed-state guard, `AlertEvaluator`'s recovery from one rule losing a concurrency race, `OutboxDispatcher`'s publish-then-mark-dispatched ordering against a substituted `IEventPublisher` |
| Market-data unit (slice 6) | xUnit, against `FakeTimeProvider` and a scripted `HttpMessageHandler` | `YahooQuoteClientTests` — a captured real `v8/finance/chart` response fixture parsed for `regularMarketPrice`, an unparseable/priceless/non-positive body or a non-success status all yielding `null` rather than throwing, the request URL built correctly; `YahooSymbolsTests` — `IVV ⇄ IVV.AX` both directions, a foreign-exchange symbol (`IVV.NZ`) rejected; `MarketDataResilienceTests` (`RealTime/MarketDataResilienceTests.cs`) — the retry/breaker/timeout pipeline built once with `MarketDataResilience.Configure` and driven entirely by a `FakeTimeProvider`, no wall-clock waiting: a transient failure retried within one execution, sustained failures opening the breaker so a subsequent call fails fast **without invoking the callback**, the breaker half-opening after its break duration and recovering on a successful probe, a hung attempt cut by the per-attempt timeout; `YahooPriceFeedServiceTests` — the real service end to end over a plain `HttpClient` (no resilience pipeline composed in, since Task 3 already proves that in isolation): one tick per seeded symbol per poll with observation-time timestamps, a bad quote skipped while every sibling symbol still ticks, a failed poll writing nothing with the next poll self-healing, and the service stopping promptly |
| Architecture | xUnit + reflection | `Domain` references nothing outside the BCL; `MarketPulse.Application` references no messaging or web framework; `MarketPulse.Alerts` references no web framework; every CSRF-exempt hub declares no client-invokable methods |
| Backend integration | WebApplicationFactory + Testcontainers | Real SQL Server **and RabbitMQ**, real migration, real HTTP; auth journeys, CSRF, rate limiting, cross-user isolation — now extended to alert rules, notifications, portfolios and transactions; the alerts pipeline end to end (rule creation → crossing tick → outbox row → dispatch → idempotent consumption → per-user SignalR delivery), redelivery producing no duplicate, a poison message reaching the dead-letter queue, an alert naming a non-existent user reaching it too rather than requeueing forever, and — through a loopback proxy that severs and restores the link to the broker — a recovering connection surviving and a consumer resubscribing after an outage; the chaos test (`ChaosTests.cs`, below); the portfolio flow over HTTP (`PortfolioApiTests.cs` — buy then partial sell producing correct average cost and realised P&L, empty portfolio 200, oversell 422, unknown ticker and bad side 400s, transaction paging newest-first) and its EF round trip (`PortfolioPersistenceTests.cs` — owned collections, `RowVersion`/`Version` tokens, decimal precision surviving); stored-key idempotency (`IdempotencyTests.cs`, below); the portfolio concurrency anomaly pair (`PortfolioConcurrencyAnomalyTests`, below); and `MarketDataCompositionTests.cs` (slice 6) — asserted against the built host's registered `IHostedService`s, no network: `MarketData:Source` explicitly set to `Fake` hosts exactly `FakeTickService`, `=Yahoo` hosts exactly `YahooPriceFeedService`, and an unrecognised value (`Chaos`) fails startup with `OptionsValidationException` naming `MarketDataOptions.Source` rather than silently falling back to the fake — note the *unset*-`Source` case (the actual production default, since `appsettings.json` under test pins `Source=Fake` explicitly) is not itself exercised; `MarketDataOptions.Source`'s `[Required]`/init-default behaviour covers that at the options level, and the case-insensitive `Source` comparison (`OrdinalIgnoreCase`, both in `AddInfrastructure`'s early read and `MarketDataOptions.Validate`) is code-level only, likewise untested |
| Frontend unit | Vitest | Stream reducer, zod schemas (including `portfolioSchema`/`transactionSchema` against 5a's wire shapes), `useNow`/`usePriceStream`/`PriceCell` hooks (stale-clock, initial-connect-failure), `useNotificationStream` (parse, cache prepend, reconnect dispatch — both hooks now share `features/realtime/reconnectPolicy`), `AlertCell` and `NotificationBell` behaviour, `HoldingsTable`'s render-time unrealised P&L derivation (positive and negative, the em-dash for a holding whose ticker hasn't ticked, the same em-dash on the footer total the moment any held ticker lacks a price), `TradeForm`'s idempotency-key lifecycle — a fresh `crypto.randomUUID()` per submission, the same key reused when Retry resubmits after a 409 *or* a network failure (no HTTP status — the other ambiguous outcome the key exists for), a new one minted on the next deliberate submission, the in-flight guard against a stray double-submit — the half of the 5a contract the server-side tests can't see, `TransactionHistory`'s load-more paging (newest-first, the button disappearing once a short page drains the history, rows and button staying mounted-and-disabled through an in-flight page fetch, an error state replacing the false "No trades recorded yet." empty state on a failed fetch), `usePortfolio`/`useTransactions`/`useRecordTransaction` (skip-page paging via `useInfiniteQuery`, each page's `take` fixed at `pageSize` and proven never to grow even after repeated load-more — the property a prior growing-window `take = pages × pageSize` shape broke against the server's `Take ≤ 100` cap — the mutation invalidating both `['portfolio']` and `['transactions']`), `Nav` (marks the active route, renders nothing without a session), api-client CSRF and single-flight refresh |
| Frontend integration | Vitest + RTL + MSW | Watchlist screen render, 409-duplicate error path, login error paths, protected-route redirect |
| End-to-end | Playwright + Chromium | Five journeys through the real API and the production dashboard build, one of them (`alerts.spec.ts`) against the full alerts pipeline including a real Alerts worker and RabbitMQ, another (`portfolio.spec.ts`) a buy and a higher sell producing correct holdings, average cost, realised P&L, and newest-first history, surviving a reload |

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
`PriceConsumer` — with its scoped `AlertEvaluator` collaborator invoked per tick, composed
exactly as `MarketPulse.Alerts`'s `Program.cs` composes it — against Testcontainers SQL
Server and RabbitMQ. `RabbitMqFixture`
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

## Idempotency

`IdempotencyTests.cs` (`MarketPulse.IntegrationTests`) exercises `IdempotencyFilter` against
both endpoints it guards, `POST /portfolio/transactions` and `POST /alerts`, over real HTTP:
replaying a fresh key returns the stored response and records exactly one transaction; the
same key with a different request body is rejected 422 `idempotency-key-reuse`; keys are
scoped per user, so Alice's key never replays for Bob; a failed request stores nothing, so the
same key is safe to retry after a 422 or 500; two concurrent requests racing one fresh key
execute exactly once — the loser gets either the winner's replayed response or a 409
`idempotency-in-flight` to retry, and `Two_concurrent_requests_with_one_fresh_key_execute_exactly_once`
asserts exactly that (one `Created`, the other `Created` or `Conflict`) rather than which one
the loser lands on, since the recursive re-check usually beats the winner's handler to
completion and 409 is the common case in practice; and a request with no header at all
executes normally every time, since the header is an offer, not a demand.
Retention and stale-claim reclaim are not built in this slice (ADR-004's consequences record
why) and so are not tested — see "Deliberately not tested," below.

## Concurrency anomaly pair

`PortfolioConcurrencyAnomalyTests` (`MarketPulse.IntegrationTests`) is the executable form of
[ADR-004](adr/004-cqrs-scope.md)'s isolation-level discussion — the coverage map's
"serializable vs read-committed demonstrated" claim made real against a running SQL Server
rather than argued in prose.

The obvious way to prove a lost update — two concurrent sells overselling a holding into
negative units — turns out not to be reachable through this race. EF's owned-collection
mapping writes `Holding.Units` as the aggregate's current absolute balance
(`SET Units = @currentValue`), computed once in memory from whichever snapshot the context
loaded, never a compounding `SET Units = Units - @amount`. Two racers validating against the
same stale snapshot each compute a value that is, on its own, always within
`[0, snapshot]` — whichever commits last simply overwrites with its own in-range number, so a
literal negative balance is mathematically unreachable for any choice of amounts. Establishing
this reframed what the test actually had to prove: not "the balance goes negative" but "a
trade that correct, non-racing validation would have rejected is instead silently accepted,
erasing the other trade's effect with no sign anything was lost."

**Test (a)**, `Without_the_token_two_racing_sells_both_succeed_though_one_should_have_been_rejected`,
proves exactly that. Two contexts load the same 10-unit holding; both sell (7 and 6 units)
against that shared stale snapshot and both validate — 7 ≤ 10 and 6 ≤ 10 — even though
7 + 6 = 13 exceeds the 10 ever held, so a correctly serialised pair could never both succeed.
The first commit lands normally. The proof step replays the second racer's *exact* trade
against the state the first commit actually left behind — a fresh, honest read, no bypass — and
it is rejected with `InsufficientHoldingsException`: that is what should have stopped the
second sell. The bypass — handing the second context the winner's current `RowVersion` so its
`UPDATE`'s `WHERE` clause matches, standing in for what every write would look like without the
token — lets it commit anyway: units land at 4 (10 − 6) rather than 3 (10 − 7), the first
sell's effect gone without a trace, and `Portfolio.Version` shows the same lost update (both
racers incremented from 1, landing at 2 instead of the 3 a correctly serialised pair would
reach).

**Test (b)**, `With_the_token_the_second_sell_loses_with_a_concurrency_exception`, removes the
bypass. The identical race — two contexts, two racing sells, no token manipulation — now
throws `DbUpdateConcurrencyException` on the loser's `SaveChangesAsync`, which
`ExceptionHandlingMiddleware` maps to 409 `concurrent-update` on the real API path; the
winner's state is correct and untouched.

The pair only works because of `Portfolio.Version` (ADR-004, decision 2): a buy/sell mutates a
`Holding`, a separate table via the owned-collection mapping, so without a scalar on the
`Portfolios` row that changes on every trade, nothing would ever force an `UPDATE` against that
row — and `RowVersion`, checked only on that `UPDATE`, would never enter a trade's `WHERE`
clause at all. Without `Version`, test (b) would not throw.

## End-to-end

`tests/e2e` covers five journeys against a real API, a real SQL Server, and — since 4b —
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
5. **(`portfolio.spec.ts`, added in 5b)** Register, navigate to `/portfolio` over the header
   nav, buy 10 units of IVV at a user-typed $60, confirm the holdings row's units and average
   cost in cell-scoped assertions (a row-wide substring match on `'6'` would be silently
   satisfied by the `$60.00` average-cost cell even before a sell ever landed), sell 4 at
   $70 and see average cost unchanged but realised P&L read `+$40.00`, confirm the live price
   cell ticks within the watchlist journey's tolerant 15s window, see the two trades
   newest-first in history, and confirm everything — holdings and history alike — survives a
   full page reload because it is server truth, not client state.

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
alerts journey needs and the other four tolerate, since nothing in them touches the
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

- **No live call to Yahoo in any automated suite (slice 6).** CI must not depend on an
  unofficial third party's uptime, rate limits, or response shape — a flaky CI run because
  Yahoo Finance is slow or briefly down would be a self-inflicted failure with no bearing on
  this codebase. `YahooQuoteClientTests` and `YahooPriceFeedServiceTests` run entirely
  against a scripted `HttpMessageHandler`; `MarketDataResilienceTests` runs entirely against
  a `FakeTimeProvider`; `MarketDataCompositionTests` pins `MarketData:Source=Yahoo` to a
  one-hour `PollInterval` specifically so no poll fires against the real `BaseUrl` while the
  `TestServer` process is up. The one place the real endpoint is exercised at all is the
  manual rehearsal (slice 6's second done criterion) — run by hand, outside CI, evidence
  recorded separately, never assumed.
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
- **No idempotency-key retention or expiry test.** Neither is built in this slice —
  `IdempotencyKeys` rows live forever, and a claim orphaned by a hard crash between claim and
  completion has no reclaim path — so there is nothing to test against; both are named as a
  deferred operations concern in ADR-004's consequences.
- **No server-side unrealised P&L test.** It does not exist server-side by design (the
  server holds no current price to compute it against — see ADR-004's context and the 5a
  spec's decision table); the derivation is client-side, and `HoldingsTable.test.tsx`
  (frontend unit, above) is where it is tested instead.
- **No concurrent-sell 409 exercised through the browser (5b).** `PortfolioConcurrencyAnomalyTests`
  and the idempotency middleware facts above already own that path; `portfolio.spec.ts` stays
  deterministic on user-typed prices instead of trying to race a real client against itself.
- **No idempotency replay at the HTTP layer from the client (5b).** `IdempotencyTests.cs`
  above owns that; the frontend suites assert the *key discipline* — the right key sent at
  the right time — which is the half of the 5a contract the server-side tests cannot see.
- **No load/perf test of history paging (5b).** A different exercise than this slice scoped.

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
