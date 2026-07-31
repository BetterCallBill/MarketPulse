# Testing strategy

Trophy-shaped: heaviest at integration, thin but present at unit and end-to-end.

## What runs

| Level | Tooling | Scope |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants, `RandomWalk` bounds |
| Application unit | xUnit + NSubstitute | Handler orchestration with a substituted repository |
| Architecture | xUnit + reflection | `Domain` references nothing outside the BCL |
| Backend integration | WebApplicationFactory + Testcontainers | Real SQL Server, real migration, real HTTP |
| Frontend unit | Vitest | Stream reducer, zod schemas, `useNow`/`usePriceStream`/`PriceCell` hooks (stale-clock, initial-connect-failure) |
| Frontend integration | Vitest + RTL + MSW | Watchlist screen, optimistic add, 409 error path |

## Deliberately not tested in slice 1

- **No E2E suite.** Playwright arrives with real authentication in slice 2. A journey
  through a stubbed login tests the stub.
- **No coverage threshold.** A threshold over a codebase this small drives noise, not
  quality. It arrives when there is a codebase to threshold.
- **No SignalR transport unit test.** That would test Microsoft's library. The one
  integration test in `PriceStreamTests` proves our chain end to end; the reducer tests
  cover our logic.
