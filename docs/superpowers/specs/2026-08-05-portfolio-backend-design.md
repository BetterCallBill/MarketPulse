# Slice 5a — Portfolio backend: "A portfolio that cannot lose a transaction"

**Date:** 2026-08-05
**Depends on:** slice 2 (auth), slice 1 (persistence patterns). Nothing from the messaging path.
**Branch:** `feature/slice-5a-portfolio-backend` → `test`

---

## Why this slice exists

The README's first sentence sells "a real-time ASX ETF portfolio & alerts platform," and its
product table promises "simulated buy/sell with idempotency keys, P&L calculation." The
alerts half of that sentence is now built and proven; the portfolio half does not exist — no
`Portfolio` type, no transaction, no P&L anywhere in the codebase. The roadmap calls this the
largest remaining gap between the README's product description and the running application.

This slice closes the backend half of that gap. It is deliberately backend-only, on the 4a/4b
precedent: the aggregate, the transaction flow, cost basis and realised P&L, the idempotency
mechanism, and the concurrency anomaly tests land here, proven by unit and integration tests;
the dashboard surface, unrealised P&L, and the Playwright journey are slice 5b, built against
an API that already works.

It also settles two debts other slices parked here:

- **Idempotency keys on `POST /alerts`** — deferred from 4a with the note that request-dedupe
  "belongs beside transaction idempotency where the money is." The money is now here.
- **ADR-004 (CQRS scope)** — the README cites it; it does not exist. The Portfolio module is
  where the CQRS question becomes concrete, so the ADR gets written here, honestly.

### Decisions taken, with rejected alternatives

| Decision | Chosen | Rejected because |
|---|---|---|
| Cost basis method | **Weighted average cost per holding.** Sells realise P&L against the holding's average cost; buys re-average it | FIFO parcels are the ATO's CGT default and the richer domain model, but they roughly double the aggregate (parcel entities, consumption ordering, parcel-level tests) for a mock portfolio whose product promise is "P&L calculation," not tax reporting. Average cost is what AU broker apps display and is a pure function of the transaction stream. Both-selectable via strategy was rejected as scope without a user |
| Concurrency control | **`RowVersion` optimistic concurrency on the portfolio row**, the house pattern `AlertRule` established. A losing concurrent write is a 409 | The README says "serializable vs read-committed demonstrated in the mock order placement flow." Running production writes in serializable transactions demonstrates isolation levels literally, but imports deadlock-and-retry handling the rest of the codebase avoids and diverges from the established precedent. The demonstration the README promises moves into the **anomaly tests and ADR-004**: the oversell anomaly reproduced under read-committed with the token bypassed, prevented with it — the claim is kept, the mechanism is honest |
| Trade price | **User-supplied fill price on every transaction**, validated positive | Executing at "the current market price" requires the server to hold one, and it deliberately does not — ticks are broadcast, not persisted, and 4a's outbox decision already rejected durable price state. A mock portfolio is a record of trades the user says they made, which is exactly how real portfolio trackers work. Backdating is allowed for the same reason |
| Portfolio creation | **Implicit: created on the user's first transaction** | An explicit `POST /portfolio` adds an API call, an empty-state, and a "you must create a portfolio first" error for zero product value. One portfolio per user is an invariant, not a resource lifecycle |
| Idempotency mechanism | **A stored-key table behind an action filter**: `Idempotency-Key` header, response replayed on repeat, 422 on same-key-different-body | Doing it inside each handler couples request-dedupe to domain logic and must be re-built per endpoint; the filter applies uniformly to `POST /portfolio/transactions` and retrofits onto `POST /alerts` in one move. Middleware (rather than a filter) was rejected because the replay needs model-binding and the route's user scope, which the action pipeline already has |
| Unrealised P&L | **Not computed server-side; 5b derives it client-side from the live price stream** | The server would need a current price per ticker, which it does not hold (see trade price). The client already holds live prices for every visible ticker; holdings × (live − average cost) is a render-time derivation — the same argument ADR-007 made for the unread badge |
| Domain events from Portfolio | **None in this slice** | The README's diagram shows Portfolio domain events driving the outbox, but nothing consumes a portfolio event today. Building the event path without a consumer is the same speculative-infrastructure mistake 4a's spec called out elsewhere. Named out of scope; the outbox is proven and waiting when a consumer exists |

---

## Scope boundary

### In scope

| Layer | Deliverable |
|---|---|
| Domain | `Portfolio` aggregate (per-user, `RowVersion`, implicit creation): `Holding` (ticker, units, average cost, cumulative realised P&L), append-only `Transaction` (side, units, price, `OccurredUtc`), `RecordBuy`/`RecordSell` with re-averaging and oversell rejection; `PortfolioExceptions` (`InsufficientHoldingsException` → 422, etc.) |
| Application | `RecordTransactionCommand` (+ FluentValidation: known ticker, positive units, positive price), `GetPortfolioQuery` (holdings + realised P&L), `GetTransactionsQuery` (`?take=` default 50 max 100, `?skip=` — the 4a notifications shape); `IPortfolioRepository`, `IIdempotencyStore` abstractions |
| Infrastructure | EF configuration + migration for `Portfolios`/`Holdings`/`Transactions`/`IdempotencyKeys`; repository and store implementations |
| Api | `PortfolioController` (`POST /api/v1/portfolio/transactions`, `GET /api/v1/portfolio`, `GET /api/v1/portfolio/transactions`); `IdempotencyFilter` (header `Idempotency-Key`: stores user + key + request hash + serialized response, replays on repeat, 422 on hash mismatch, optional on endpoints that opt in); the filter applied to `POST /portfolio/transactions` **and** `POST /alerts` |
| Testing | TDD on the aggregate; the oversell **anomaly test pair**; idempotency integration tests; persistence, API, and cross-user isolation tests in the house patterns (tabulated below) |
| Docs | **ADR-004** (CQRS scope, honest: MediatR commands/queries over one store, no separate read model, and why that is the right amount of CQRS here); README/ROADMAP rows this slice closes; `TESTING.md` |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

All portfolio UI and unrealised P&L (5b) · portfolio domain events and any messaging · FIFO
parcels or CGT reporting · dividends, fees, brokerage, cash balances · multiple portfolios per
user · portfolio notes (its own slice; the README's DOMPurify story) · price lookups or
execution at market · deleting/amending transactions (append-only; an incorrect trade is
corrected by an offsetting one — noted in ADR-004's consequences) · idempotency on any
endpoint beyond the two named.

---

## Domain model

`Portfolio` follows `Watchlist`'s shape (private constructor, static `Create`, invariants
throw `DomainException` subclasses carrying status + error codes) with `AlertRule`'s
concurrency posture (`RowVersion`).

- **`Portfolio`** — `Id`, `UserId` (unique), `RowVersion`, `IReadOnlyCollection<Holding>`,
  `IReadOnlyCollection<Transaction>`.
  - `RecordBuy(ticker, units, price, occurredUtc)` — finds or creates the holding;
    `newAvg = (units·avg + boughtUnits·price) / (units + boughtUnits)`; appends the
    transaction.
  - `RecordSell(ticker, units, price, occurredUtc)` — throws `InsufficientHoldingsException`
    if `units > holding.Units` (or no holding); realised P&L `+= units·(price − avg)`;
    average cost unchanged by a sell; a holding sold to zero units is retained with its
    cumulative realised P&L (the history is the product); a subsequent buy re-averages from
    zero units, so the basis resets — by construction, not by special case.
- **`Holding`** — child of the aggregate, no public mutators.
- **`Transaction`** — immutable record of `Side` (`Buy`/`Sell`), `Units`, `Price`,
  `OccurredUtc`, `RecordedUtc`. Append-only: the aggregate exposes no removal, the API no
  delete.

Money and units are `decimal` throughout. Precision: units `decimal(18,6)` (ETFs sell
fractional units), prices and P&L `decimal(18,4)`, consistent with the tick pipeline's
existing price handling.

## API surface

| Endpoint | Behaviour |
|---|---|
| `POST /api/v1/portfolio/transactions` | Body `{ ticker, side, units, price, occurredUtc? }` (side a string, `"Buy"`/`"Sell"`, validated like alert direction; `occurredUtc` defaults to now). 201 with the updated portfolio summary. `Idempotency-Key` header honoured. 422 on oversell; 409 on a lost concurrency race (client retries) |
| `GET /api/v1/portfolio` | Holdings (ticker, units, average cost, realised P&L) + portfolio totals. An empty portfolio is 200 with empty holdings, not 404 — implicit creation means "no portfolio yet" is not a state the API exposes |
| `GET /api/v1/portfolio/transactions` | Newest first, `?take=`/`?skip=`, the notifications paging shape |

`POST /alerts` gains the same `Idempotency-Key` handling, changing nothing else about it.

## Idempotency mechanism

`IdempotencyKeys` table: `UserId`, `Key`, `Endpoint`, `RequestHash` (SHA-256 of the body),
`ResponseStatusCode`, `ResponseBody`, `CreatedUtc` — unique on (`UserId`, `Endpoint`, `Key`).
The filter, on endpoints that opt in via attribute:

1. No header → execute normally (the header is an offer, not a demand — existing clients keep
   working; 5b's UI will always send one).
2. Header present, no stored row → execute; store status + body only when the action
   **succeeds** (2xx). A failed request stores nothing, so retrying the same key after a 422
   or 500 re-executes — failures are safe to repeat, and replaying a stored failure would
   pin a client to an error it may have already fixed.
3. Stored row, matching hash → replay stored status + body without executing. At-most-once
   from the client's view.
4. Stored row, different hash → 422 `idempotency-key-reuse`: the client is confused, and
   silently executing a different request under a replayed key is the one unforgivable
   outcome.

Two requests racing the same fresh key: the unique index decides; the loser reads the
winner's stored response (or 409s if the winner hasn't committed — retried by the client like
any 409). Keys are never expired in this slice; a retention policy is an operations concern
named in ADR-004's consequences, not built speculatively.

## Error handling

- Oversell → `InsufficientHoldingsException` (422, `insufficient-holdings`, message carries
  held vs requested units) through the existing `ExceptionHandlingMiddleware`.
- Concurrency loss → `DbUpdateConcurrencyException` mapped to 409 `concurrent-update` (the
  4a `RowVersion` handling pattern); the client's retry re-reads and re-validates, so a
  retried sell that now oversells fails correctly.
- Unknown ticker / bad side / non-positive units or price → FluentValidation 400s with named
  error codes, the alerts pattern.
- Idempotency conflicts as above.

## Testing

| Suite | What |
|---|---|
| xUnit (unit, TDD) | Aggregate: buy creates holding; buy re-averages (worked example in the test name's numbers); sell realises against average; sell-to-zero retains history and a rebuy resets basis; oversell throws; sell with no holding throws; averaging precision at fractional units |
| xUnit (integration, persistence) | Round-trip the aggregate through EF (owned collections, `RowVersion` token, decimal precision survives) |
| xUnit (integration, **anomaly pair**) | The README's demonstration: two concurrent sells of the same holding, (a) with the concurrency token bypassed under read-committed → both commit, units go negative — the anomaly reproduced and asserted; (b) through the real path → one 409, units correct. The pair is the executable form of ADR-004's isolation discussion |
| xUnit (integration, idempotency) | Same key replays the stored response without a second transaction; same key different body → 422; keys scoped per user (Alice's key does not replay for Bob); the race on a fresh key yields exactly one execution; `POST /alerts` honours the same header |
| xUnit (integration, API) | Transaction flow over HTTP (buy, sell, portfolio reflects both); paging shape on transactions; empty portfolio 200; cross-user isolation extended to portfolio and transactions (the slice-2 suite's pattern) |

### Deliberately not tested

Load on the idempotency table · retention/expiry (not built) · unrealised P&L (does not exist
server-side) · UI flows (5b).

---

## Done criteria

1. All existing suites green; new unit + integration tests green (`dotnet test` from clean
   checkout with compose infra up).
2. A buy and a partial sell recorded over HTTP produce correct average cost and realised P&L,
   asserted end to end.
3. The anomaly pair passes: oversell demonstrated without the token, prevented with it.
4. Replaying `POST /portfolio/transactions` with the same `Idempotency-Key` creates exactly
   one transaction and returns an identical response; `POST /alerts` behaves the same way.
5. ADR-004 exists; the README's CQRS, idempotency, and isolation claims all point at built,
   tested code; ROADMAP and TESTING.md updated.
6. `DependencyRuleTests` stays green; no new references from Application/Domain outward.

---

## Interview category coverage

| # | Category | What this slice banks |
|---|---|---|
| 7 | **Web API design** ⭐ | Idempotency keys designed and built (header semantics, replay, hash-mismatch 422, race behaviour) — the README claim made real on two endpoints |
| 8 | **Data access & SQL Server** ⭐ | The anomaly pair: a write anomaly reproduced and prevented, with the isolation-level discussion in executable form; owned-entity mapping, decimal precision, unique-index race semantics |
| 10 | **Architecture & system design** | ADR-004 as the worked example of *scoping* a pattern — CQRS kept to MediatR-over-one-store with the rejected fuller version documented; append-only ledger as a domain decision |
| 1 | **C# & .NET** | An aggregate with real invariant arithmetic under TDD; `decimal` precision decisions |

---

## What comes next

**Slice 5b** — the portfolio dashboard: holdings table with live unrealised P&L derived
client-side from the price stream (the ADR-007 pattern), the buy/sell form sending
idempotency keys, transaction history, and the Playwright journey. After that the roadmap's
phase-2 story (real market data, slice 6) inherits a portfolio worth pricing accurately.
