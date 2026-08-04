# Testing strategy

Trophy-shaped: heaviest at integration, thin but present at unit and end-to-end.

## What runs

| Level | Tooling | Scope |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants, `RandomWalk` bounds, `User` lockout, `RefreshToken` rotation, `AlertRule.Evaluate`'s truth table across both directions and boundary equality, its `MarkTriggered`/`Rearm` transitions and their illegal counterparts |
| Application unit | xUnit + NSubstitute | Handler orchestration with a substituted repository, password policy, `TickBroadcaster`'s sink fan-out (including one sink throwing), `RabbitMqTickSink`'s non-blocking/single-flight reconnect behaviour, `OutboxDispatcher`'s publish-then-mark-dispatched ordering against a substituted `IEventPublisher` |
| Architecture | xUnit + reflection | `Domain` references nothing outside the BCL; `MarketPulse.Application` references no messaging or web framework; `MarketPulse.Alerts` references no web framework; every CSRF-exempt hub declares no client-invokable methods |
| Backend integration | WebApplicationFactory + Testcontainers | Real SQL Server **and RabbitMQ**, real migration, real HTTP; auth journeys, CSRF, rate limiting, cross-user isolation — now extended to alert rules and notifications; the alerts pipeline end to end (rule creation → crossing tick → outbox row → dispatch → idempotent consumption → per-user SignalR delivery), redelivery producing no duplicate, and a poison message reaching the dead-letter queue |
| Frontend unit | Vitest | Stream reducer, zod schemas, `useNow`/`usePriceStream`/`PriceCell` hooks (stale-clock, initial-connect-failure), api-client CSRF and single-flight refresh |
| Frontend integration | Vitest + RTL + MSW | Watchlist screen render, 409-duplicate error path, login error paths, protected-route redirect |
| End-to-end | Playwright + Chromium | Three journeys through the real API and the production dashboard build |

**TDD in practice.** `AlertRule.Evaluate` is the concrete example behind the "TDD used for
the alert-evaluation engine" claim in the README's coverage map: its truth-table unit tests,
and each integration test across the alerts pipeline (`RabbitMqTopologyTests`,
`AlertEvaluationTests`, `OutboxDispatchTests`, `AlertPipelineTests`), were written and
confirmed failing — either against not-yet-existing types or a not-yet-existing project —
before the implementation that made them pass existed. `Evaluate` is a pure function of
`(rule, tick)` with no prior-price memory, which is what makes a truth table the right shape
of test for it in the first place.

## End-to-end

`tests/e2e` covers three journeys against a real API and a real SQL Server:

1. Register a new account, land on the protected watchlist, add a ticker, see its price
   tick from SignalR, sign out, and confirm `/` is genuinely closed afterwards.
2. Reload the page mid-session and stay signed in — the point of keeping the token in a
   cookie rather than in JavaScript memory.
3. Sign in with the wrong password and get an error without leaving the login page.

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
reports liveness rather than readiness, Playwright concludes the server is up, and all
three specs then fail on queries against a database that does not exist.

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
- **No chaos test.** Killing the broker mid-flow and proving recovery is slice 4b's job —
  it is the test that turns "the outbox exists" into "no alert was lost". This slice proves
  the outbox and the dead-letter path exist; it does not yet prove they survive an outage.
- **No load test on evaluation throughput.** The random walk emits four ticks a second.
  Measuring throughput belongs to slice 12, against a feed and a rule count actually worth
  measuring.
- **No multi-instance worker test.** `AlertRule`'s `RowVersion` concurrency token — the
  mechanism that makes two worker instances a supported configuration rather than a
  double-notification bug — is exercised by forcing a concurrency conflict inside one test
  process (`Two_concurrent_evaluations_of_one_rule_produce_one_outbox_row`), not by running
  two worker processes against each other in CI.

## Shared-fixture determinism

`RabbitMqTopologyTests`, `AlertEvaluationTests`, `OutboxDispatchTests`, and
`AlertPipelineTests` all share one SQL Server container and one RabbitMQ container across
the whole `MessagingCollection`, never reset between test classes — the same pattern the
rest of the integration suite already uses for `SqlServerFixture`. Two consequences that
shaped how these tests were written, not just what they assert:

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
