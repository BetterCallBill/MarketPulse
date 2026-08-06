# Slice 7a — Price history backend

**Date:** 2026-08-06
**Status:** Approved
**Slice:** 7a of the roadmap's slice 7 (*Price history and charts*, phase 2, flagged "may
split" — this is the split: 7a is the backend, 7b is the chart UI)

## Why this slice exists

Slice 6 made prices real; nothing keeps them. Every tick is broadcast and forgotten, so
there is no history to chart, and three README promises remain unbacked: Dapper-backed
history queries, the deliberate N+1 postmortem (ADR-006), and the SQL execution-plan
analysis in `docs/sql/`. This slice persists the tick stream, serves OHLC candles and
watchlist sparklines from it through Dapper, and manufactures — then measures, then fixes —
the N+1 those artifacts require. 7b renders what this slice serves.

### Decisions taken, with rejected alternatives

1. **Aggregate on read over a bounded raw table.** Candles are computed at query time by a
   Dapper `GROUP BY` over raw ticks; a retention job keeps the raw table bounded. *Rejected:*
   precomputed 1-minute candles on write (adds a stateful rollup component, and the raw
   table — still wanted for the postmortem — needs retention anyway); raw + continuous
   rollup (most production-realistic, most moving parts, exceeds the slice's task budget).
   The on-read `GROUP BY` is also, deliberately, the query whose execution plan `docs/sql/`
   analyses.
2. **EF Core owns the write side, Dapper the read side.** The schema, migration and flush
   go through the existing `MarketPulseDbContext`; the read path is the codebase's first
   Dapper code, behind an Application-owned interface. One nuance, stated up front: the
   flush itself is a parameterised set-based `INSERT` via `ExecuteSqlRawAsync`, not
   `AddRange` — change tracking buys nothing for append-only rows, and EF's rows-affected
   propagation check would misread `IGNORE_DUP_KEY`'s silently-dropped duplicates as a
   `DbUpdateConcurrencyException`. That observation is part of the EF-vs-Dapper write-up,
   not a footnote. *Rejected:* Dapper both ways (loses the comparison the README promises);
   EF both ways (no Dapper story at all); `SqlBulkCopy` (a third data-access idiom for a
   ~125-row flush is machinery without a payoff).
3. **The N+1 is real, shipped, and then fixed.** The sparklines endpoint's first
   implementation loops the watchlist calling the single-ticker query — committed as such,
   measured, then replaced by one set-based query, with both measurements kept. *Rejected:*
   staging the N+1 inside the candle path (contrived — no one would naturally write it that
   way); writing ADR-006 about the risk only (breaks the roadmap's promise of a postmortem
   with real numbers).
4. **Natural composite key, no surrogate.** `PriceTicks` is keyed `(Ticker, TimestampUtc)`,
   clustered, with `IGNORE_DUP_KEY = ON`. An identity column on an append-only time series
   buys nothing and costs the clustered range-scan layout that every read wants. Duplicate
   observations (Yahoo re-serving an observation timestamp across polls) are dropped by the
   database rather than deduplicated in application code, so a replayed tick cannot poison a
   batch. *Rejected:* in-memory last-seen-per-ticker filtering (a second dedup mechanism to
   test, and wrong across restarts).
5. **Retention from day one.** `RefreshTokens` still grows forever and Q8.5 records that as
   a defect; this slice does not repeat it. Raw ticks older than 7 days are deleted hourly,
   in chunks. (Sweeping `RefreshTokens` itself stays out of scope — it is category-13 debt,
   not price history — but the pattern now exists in the codebase.)

## Scope boundary

### In scope

- `PersistingTickSink` (`ITickSink`) and `TickPersistenceService` — buffered, batched tick
  writes.
- `PriceTicks` table + migration, `TickRetentionService`, `HistoryOptions`.
- Dapper (new pinned dependency), `IPriceHistoryReader` in Application, Dapper
  implementation in Infrastructure.
- `GET /api/v1/prices/{ticker}/candles` and `GET /api/v1/prices/sparklines`, MediatR
  handlers, validation, ProblemDetails error slugs.
- The deliberate N+1: v1 loop → measurements → set-based fix → re-measurements → ADR-006 →
  `docs/sql/`.
- Integration tests (Testcontainers), including a query-count pin on the N+1 fix.

### Out of scope

- The chart UI, chart library selection, lazy loading, client zod schemas — slice 7b.
- `stale-while-revalidate` caching — TanStack Query's job in 7b; no `Cache-Control` work
  here.
- Precomputed candle tables, intraday candle streaming over SignalR, volume/bid/ask (the
  tick has none).
- `RefreshTokens` retention (see decision 5).

## Architecture

### Write path

`TickBroadcaster` already fans out to `ITickSink[]` and does not change. The new sink must
not block the broadcast loop — sinks run sequentially per tick, and a database write per
tick would tax the SignalR path — so:

- **`PersistingTickSink`** only writes the tick into its own bounded in-memory buffer
  (capacity 5,000, `DropOldest` with a warning log — under sustained database outage the
  history gets a hole; the live stream must not stall). `SendAsync` completes synchronously
  in the normal case.
- **`TickPersistenceService`** (`BackgroundService`) drains the buffer and flushes a batch
  every 5 seconds or 500 rows, whichever comes first, with a final flush on shutdown. Each
  flush creates a DI scope via `IServiceScopeFactory` and resolves the `DbContext` inside
  it — the service is effectively a singleton, and constructor-injecting a scoped context
  here is precisely the captive-dependency trap Q4.3 warns about. The flush executes one
  parameterised multi-row `INSERT` through `ExecuteSqlRawAsync` (see decision 2 for why not
  `AddRange`). Rates for calibration: Fake mode produces 25 ticks/sec (batches of ~125),
  Yahoo mode ~25/min.

The row type (`PriceTickRow`) lives in Infrastructure and exists for the migration and
model only — persistence of the stream is an infrastructure concern; Domain's `PriceTick`
value object is unchanged.

### Schema

```sql
CREATE TABLE PriceTicks (
    Ticker       nvarchar(8)    NOT NULL,
    TimestampUtc datetime2      NOT NULL,
    Price        decimal(18,4)  NOT NULL,
    CONSTRAINT PK_PriceTicks PRIMARY KEY CLUSTERED (Ticker, TimestampUtc)
        WITH (IGNORE_DUP_KEY = ON)
);
```

Clustered on `(Ticker, TimestampUtc)` because every read is "one ticker, a time range":
range scans arrive pre-sorted and there is no secondary index to maintain. Retained volume
at the Fake-mode worst case is ~15M rows (25/sec × 7 days) — acceptable, and measured in
`docs/sql/`.

**`TickRetentionService`** runs hourly: `DELETE TOP (10000)` loops until no rows older than
the cutoff remain, chunked to avoid lock escalation and long transactions.

**`HistoryOptions`** (bound with `ValidateDataAnnotations().ValidateOnStart()`, like
`JwtOptions`): `RetentionDays` (default 7), `FlushIntervalSeconds` (5), `FlushBatchSize`
(500), `BufferCapacity` (5000), `MaxCandleBuckets` (1000), `SparklineWindowMinutes` (60).
Tests drive small values through `WebApplicationFactory` configuration, same as
`AuthOptions`.

### Read path

`IPriceHistoryReader` is defined in Application (alongside `Candle` and sparkline DTO
records); `DapperPriceHistoryReader` in Infrastructure implements it against the same
connection string. Dapper is added to `Directory.Packages.props` pinned, per the dependency
policy — it earns its place at both read queries, and the licence (Apache-2.0) is checked.

- **Candles:** OHLC per bucket, bucketed by epoch-second arithmetic
  (`DATEDIFF`/`DATEADD` from a fixed origin), open/close via window functions, high/low via
  `MIN`/`MAX`, grouped over the requested range. Intervals are allowlisted: `1m | 5m | 1h |
  1d`. Buckets with no ticks are omitted, not zero-filled — gap handling is presentation's
  problem (7b).
- **Sparklines:** last `SparklineWindowMinutes` of 1-minute closes for a set of tickers.
  Version 1 loops tickers reusing the single-ticker query (the deliberate N+1); the fix is
  one set-based query for all tickers. Both versions exist in the git history; only the fix
  survives.

### API

Both endpoints `[Authorize]`, both thin MediatR dispatch, consistent with
`WatchlistController`:

- **`GET /api/v1/prices/{ticker}/candles?interval=1m&from=…&to=…`** →
  `{ ticker, interval, candles: [ { t, o, h, l, c } ] }`, timestamps UTC ISO-8601 (bucket
  start). Errors below.
- **`GET /api/v1/prices/sparklines`** → `{ sparklines: { <ticker>: [closes…] } }` for every
  ticker on the caller's watchlist, resolved server-side via the existing
  `IWatchlistRepository` — no ticker list in the query string, nothing to tamper with.
  Empty watchlist → empty object, 200.

### Error surface

| Case | Response |
|---|---|
| Ticker not in the reference table | 404 `unknown-ticker` (allowlist, consistent with Q13.2) |
| Interval not in the allowlist | 400 `invalid-interval` |
| `from` ≥ `to`, or unparseable | 400 `invalid-range` |
| Range ÷ interval > `MaxCandleBuckets` | 400 `range-too-large` — a cap you can state beats a query you cannot bound |
| Known ticker, no data in range | 200, empty `candles` — absence of data is not an error |

All through the existing `ExceptionHandlingMiddleware` → ProblemDetails machinery; slugs are
contract, messages are not.

## The N+1 postmortem (ADR-006 + docs/sql)

Sequence, each step preserved in history:

1. Ship sparklines v1 (loop). Correctness tests pass — that is the point: tests cannot see
   this defect.
2. Populate a realistic table (retention-window volume), capture per-request timings and the
   actual execution plans at a 20-ticker watchlist. Save to `docs/sql/`.
3. Replace with the set-based query. Re-measure identically. Save alongside.
4. Write ADR-006 as a before/after postmortem: what the loop cost, why every test stayed
   green, what the fix changed in the plan.
5. Pin the fix: an integration test counts commands through a counting decorator on the
   Dapper connection factory and asserts the sparkline request issues **one** history query
   regardless of watchlist size. The regression cannot return silently.

## Testing

Trophy-shaped, integration-heavy via the existing Testcontainers fixture:

- **Candle correctness:** ticks straddling bucket boundaries land in the right buckets;
  OHLC values correct when open ≠ high ≠ low ≠ close; single-tick buckets collapse to
  o=h=l=c.
- **Dedupe:** replaying a `(Ticker, TimestampUtc)` pair leaves one row and does not fail
  the surrounding batch.
- **Retention:** old rows deleted, boundary rows retained, chunking loop terminates.
- **Persistence service:** flush at batch size, flush at interval (`FakeTimeProvider`),
  final flush on shutdown, buffer overflow drops-oldest with a warning.
- **Contract:** 404 / 400 / empty-200 paths, ProblemDetails shape and slugs asserted.
- **N+1 pin:** as above — one query at any watchlist size.
- **Cross-user:** sparklines return only the caller's watchlist tickers (extends
  `CrossUserIsolationTests`).

### Deliberately not tested

- Year-scale `GROUP BY` load behaviour — no year of data exists; `docs/sql/` records
  retention-window-scale measurements instead.
- The Dapper SQL against in-memory fakes — the queries only run against real SQL Server,
  which is the entire reason the integration fixture exists (Q14.2).

## Done criteria

- Migration applies from clean; `dotnet ef database update` and the app boot both succeed
  from a wiped database.
- Full suite green: existing tests untouched, new integration tests as listed.
- Fake-mode manual check: dashboard runs ≥ 5 minutes, `PriceTicks` row count grows,
  a candle request returns plausible OHLC, retention deletes rows under a shortened
  test-configured window.
- `docs/sql/` contains before/after timings and execution plans; ADR-006 committed.
- ROADMAP status table updated in the closing commit, per its own rule.

## Interview category coverage

Advances category 8 hardest (first Dapper read path, EF-vs-Dapper trade-off made concrete,
N+1 postmortem with plans, index design for a time-series table, retention actually built),
category 1 (a second bounded-buffer producer/consumer, batching), category 7 (two new
endpoints under the established error contract), and category 14 (the query-count pin — a
test shaped by a defect tests could not see).

## What comes next

Slice 7b: the chart UI — library selection (lazy-loaded, per the README's bundle-budget
promise), zod schemas for the candle and sparkline contracts, the `/history` route or
per-row sparklines on the watchlist, and `stale-while-revalidate` via TanStack Query.
