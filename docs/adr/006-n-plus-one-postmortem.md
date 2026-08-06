# ADR-006: N+1 postmortem — sparklines, measured, fixed, pinned

**Status:** Accepted · **Date:** 2026-08-06

## Context

Task 10 (commit `ea51348`) shipped `GetSparklinesHandler` with a query pattern chosen
deliberately, not by accident: for every ticker on the caller's watchlist, call
`IPriceHistoryReader.GetCandlesAsync` once and collect its closes.

```csharp
// DELIBERATE N+1 (ADR-006): one candle query per watchlist ticker. Correct and
// fully tested — which is the point: no test in this suite can see the defect.
var sparklines = new Dictionary<string, IReadOnlyList<decimal>>();
foreach (var ticker in tickers)
{
    var candles = await reader.GetCandlesAsync(ticker, 60, from, to, ct);
    sparklines[ticker] = candles.Select(c => c.Close).ToList();
}
```

It shipped this way to make a specific point demonstrable rather than merely assertable:
`SparklineApiTests` (three tests — correct closes for a mixed watchlist, an empty watchlist,
cross-user isolation) all passed against it, and would keep passing against it forever. Every
one of those tests asserts on the *response body*. None of them can see how many queries
produced that body, because `HttpClient` in an integration test observes only the HTTP
response — the query count is invisible from outside the process, and nothing in the test
suite looked inside. A correctness suite, however thorough, is structurally blind to an
access-pattern defect. That blindness is the actual subject of this ADR; the N+1 itself is
just the vehicle.

## Decision

1. **Replace the loop with one set-based query.** `IPriceHistoryReader.GetSparklinesAsync`
   takes the whole ticker list and returns closing prices per 1-minute bucket for every
   ticker that has data in range, in one `SELECT` — `WHERE Ticker IN @Tickers`, windowed by
   `ROW_NUMBER() OVER (PARTITION BY Ticker, Bucket ORDER BY TimestampUtc DESC)` picking the
   last tick per (ticker, bucket) pair, same closing-price semantics as before. `GetCandlesAsync`
   is untouched — the candles endpoint still needs full OHLC for a single ticker, a different
   shape this ADR has no reason to touch.

2. **`GetSparklinesHandler` fills gaps after the query, not during it.** Tickers absent from
   the query's result (no ticks in the window) get an explicit empty list rather than being
   silently dropped, so the response stays total over the watchlist — same observable
   contract as v1, verified by the unchanged `SparklineApiTests`.

3. **A query-count pin, not just a correctness test.** `SparklineQueryCountTests` wraps
   `ISqlConnectionFactory` in `CountingSqlConnectionFactory`, adds three watchlist tickers,
   and asserts `Assert.Equal(1, counter.Opened)` after one `GET
   /api/v1/prices/sparklines`. `DapperPriceHistoryReader` opens exactly one connection per
   reader method and runs exactly one query on it, so counting connections is counting
   queries — this is the test correctness testing cannot be, because it asserts on the access
   pattern the response was produced *by*, not the response itself.

## Measured: before and after

Full methodology and raw captures are in `docs/sql/` (`README.md`, `sparklines-before.txt`,
`sparklines-after.txt`, `sparklines-e2e-before.txt`, `sparklines-e2e-after.txt`); the numbers
here are quoted from those files, not estimated.

**The pin, run against the still-N+1 handler (RED, as required before the fix landed):**

```
Assert.Equal() Failure: Values differ
Expected: 1
Actual:   3
```

Three watchlist tickers, three connections opened for a single sparklines request — the N+1,
caught by a test for the first time. After the fix, the same test passes with `counter.Opened
== 1`, and the whole suite (119 integration tests, 151 unit tests) stays green.

**SQL Server statistics** (`SET STATISTICS TIME/IO ON`, 20 tickers, 404,625-row `PriceTicks`,
60-minute window — matching `HistoryOptions.SparklineWindowMinutes`'s default). The N+1
column here is a T-SQL `CURSOR` loop over one connection — a harness stand-in for the real
v1 code's 20 separate ADO.NET connections, which nothing expressible in a `.sql` script can
reproduce. That divergence matters for reading the numbers correctly, so it's stated before
them, not after: full detail in `docs/sql/README.md`'s "harness divergence" note.

| | Before (N+1, 20 queries) | After (set-based, 1 query) |
|---|---|---|
| `PriceTicks` logical reads (sum) — the actual query work | 112 | 112 |
| `Tickers`/`Worktable` logical reads (query itself, excl. cursor bookkeeping) | 0 | 2 |
| Statement batches | 69 (cursor `OPEN`/21×`FETCH`/`CLOSE` + 20 × query) | 4 |
| Ad-hoc parse & compile (once per batch, not per statement) | 144 ms | 12 ms |
| Statement execution time excl. compile (sum) | 6 ms elapsed / 17 ms CPU | 13 ms elapsed / 24 ms CPU |

**Server-side logical reads for the query work are effectively unchanged (112 vs. 114), and
excluding the one-off ad-hoc compile, the set-based query is not faster server-side at this
data volume — it is slightly slower.** `PriceTicks` is scanned 20 times either way — 20
tickers means 20 indexed seeks against the clustered `(Ticker, TimestampUtc)` index
regardless of whether one query issues all 20 or 20 queries issue one each — so SQL Server's
own cost was never where this fix's value lives. An earlier draft of this ADR attributed the
"win" to reduced `Tickers`/`Worktable` reads and a large elapsed/CPU-time multiplier; both
numbers turned out to be artifacts of the cursor harness (its `OPEN`/`FETCH` bookkeeping,
not the candle query) and of comparing two one-off ad-hoc batch compiles (not 20 compiles
collapsing to 1 — auto-parameterization already reused one cached plan across all 20 cursor
iterations). The corrected reading: **the N+1's real, measured cost is round-trip count —
20 independent connections and commands collapsed to 1** — a number `STATISTICS IO`/`TIME`
has no visibility into at all, because both measure server-side work only. That is exactly
what the end-to-end HTTP numbers below measure instead.

**End-to-end HTTP timing** (20 sequential `GET /api/v1/prices/sparklines`, 20-ticker
watchlist, same 404,625-row `PriceTicks` table for both runs — captured by reverting to the
v1 loop with `git stash`, timing it, then popping the stash and timing the fix against the
identical data, so the comparison is apples-to-apples):

| | Before (N+1) | After (set-based) |
|---|---|---|
| Steady-state average (19 warm requests) | 32.9 ms | 22.5 ms |
| Steady-state min / max | 30.7 / 36.8 ms | 20.8 / 26.0 ms |

**~32% faster end-to-end** at 20 watchlist tickers (32.9ms → 22.5ms, ~10.4ms saved per
request), and the gap is structural, not incidental: the before column pays one HTTP-visible
round trip to SQL Server per ticker, the after column pays one regardless of watchlist size.
A user with 50 tickers would not have made this measurement bigger by coincidence — the
before column's cost scales with watchlist size and the after column's does not.

An earlier attempt at this same measurement reseeded `PriceTicks` between the before and
after HTTP captures, doubling the after run's data volume relative to the before run's, and
produced a number that looked like a *regression* (67ms avg vs. 34ms) — a measurement
artifact from comparing two different databases, not a real result. It is recorded in
`docs/sql/README.md` as the reason both final captures were taken against the identical
dataset; the numbers above are the corrected, apples-to-apples pair.

## Rationale

**Why the loop stayed green.** `SparklineApiTests` was written correctly, by the standard a
correctness suite is judged against: it asserts the response contains the right tickers with
the right closes, for the right user, including the empty-watchlist and cross-user-isolation
edge cases. Nothing about that suite was careless. The defect it cannot see is categorical,
not a gap in that particular suite's coverage — an integration test driven through
`HttpClient` observes exactly one thing, the HTTP response, and a response assembled from
one query is byte-identical to the same response assembled from twenty. No amount of adding
more response-shape assertions would have found this; the axis being tested (correctness)
and the axis that was wrong (access pattern) are orthogonal.

**Why a connection-count pin, specifically.** The alternative — asserting on latency, or on a
SQL Server extended-events capture — either flakes on CI hardware variance or requires
tooling this suite doesn't otherwise depend on. `CountingSqlConnectionFactory` decorates the
same `ISqlConnectionFactory` seam the reader already uses, so the pin needs no new
infrastructure and no timing assumptions: `DapperPriceHistoryReader` opens exactly one
connection per reader method call, so connections opened during a request is queries issued
during that request, deterministically, on any hardware.

**Why the fix stays set-based rather than adding a per-ticker cache.** A cache would have
made the loop's cost invisible again without removing it — cold-cache requests still pay N
queries, and a slice about postmortem-ing an N+1 that hid behind a passing test suite should
not replace it with a cache that would hide behind a passing cache-hit-rate metric instead.
The set-based query removes the N, not the visibility of the N.

## Consequences

**The pin is now permanent architecture, not a one-off regression test.** Any future change
to `GetSparklinesHandler` or `DapperPriceHistoryReader.GetSparklinesAsync` that reintroduces
a per-ticker query will fail `SparklineQueryCountTests` immediately, with the same clarity
this postmortem needed to be written by hand: `Expected: 1, Actual: <N>`.

**The technique generalises to `GetCandlesAsync` if it ever gains a caller that loops over
tickers.** `CountingSqlConnectionFactory` is not sparklines-specific; any future N+1 over the
same `ISqlConnectionFactory` seam can be pinned the same way, cheaply.

**The corrected-comparison methodology is now documented, not just the result.** The
seed/measure/reseed-by-accident mistake in `docs/sql/README.md` is left in, not scrubbed —
future measurement work on this table should hold data volume constant across before/after
captures, and the record of getting that wrong once is cheaper insurance than getting it
wrong again silently.

**The transferable rule this ADR exists to leave behind:** correctness tests cannot see
query counts; pin the access pattern, not just the result.
