# Testing strategy

Trophy-shaped: heaviest at integration, thin but present at unit and end-to-end.

## What runs

| Level | Tooling | Scope |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants, `RandomWalk` bounds, `User` lockout, `RefreshToken` rotation |
| Application unit | xUnit + NSubstitute | Handler orchestration with a substituted repository, password policy |
| Architecture | xUnit + reflection | `Domain` references nothing outside the BCL |
| Backend integration | WebApplicationFactory + Testcontainers | Real SQL Server, real migration, real HTTP; auth journeys, CSRF, rate limiting, cross-user isolation |
| Frontend unit | Vitest | Stream reducer, zod schemas, `useNow`/`usePriceStream`/`PriceCell` hooks (stale-clock, initial-connect-failure), api-client CSRF and single-flight refresh |
| Frontend integration | Vitest + RTL + MSW | Watchlist screen render, 409-duplicate error path, login error paths, protected-route redirect |
| End-to-end | Playwright + Chromium | Three journeys through the real API and the production dashboard build |

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
- **No SignalR transport unit test.** That would test Microsoft's library. The one
  integration test in `PriceStreamTests` proves our chain end to end; the reducer tests
  cover our logic.
- **No load or brute-force simulation.** The rate limiter is tested for behaviour at its
  threshold — the tenth request passes, the eleventh is rejected — not under real
  concurrent load. Proving a fixed-window limiter holds up under contention needs a load
  harness, which is a different exercise.
- **No test that `Secure` cookies work over HTTPS.** The whole suite runs in Development,
  where `Secure` is off by design: .NET's `CookieContainer` refuses to send `Secure`
  cookies over plain HTTP, so turning it on would break every integration test rather than
  strengthen it. The attribute is instead covered by `AuthCookiesTests` as a pure function
  over the environment name.
