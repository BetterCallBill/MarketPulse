# Sparklines N+1 — measurement artifacts (ADR-006)

This directory holds the before/after evidence behind
[ADR-006](../adr/006-n-plus-one-postmortem.md): `GetSparklinesHandler` v1 (Task 10, commit
`ea51348`) issued one candle query per watchlist ticker — a deliberate, fully-tested N+1.
Everything here was captured against a real SQL Server instance (`docker compose up -d
sqlserver`), not a mock.

## Files

| File | Purpose |
|---|---|
| `seed-sparkline-history.sql` | Populates `PriceTicks` at retention-window scale: 20 tickers × 7 days × 1/minute. Uses the table's `IGNORE_DUP_KEY` primary key, so re-running it just adds another week's worth of history rather than erroring. |
| `sparklines-n-plus-one.sql` | The exact query shape v1's loop ran, once per ticker, wrapped in `SET STATISTICS TIME/IO ON`. |
| `sparklines-set-based.sql` | The fixed single-query shape (`GetSparklinesAsync`), same statistics capture, for a like-for-like comparison. |
| `sparklines-before.txt` | Captured output of `sparklines-n-plus-one.sql`. |
| `sparklines-after.txt` | Captured output of `sparklines-set-based.sql`. |
| `sparklines-e2e-before.txt` | Raw `curl -w '%{time_total}\n'` output, 20 requests, v1 (N+1) handler. |
| `sparklines-e2e-after.txt` | Raw `curl -w '%{time_total}\n'` output, 20 requests, set-based (fixed) handler, same database snapshot as the before file. |

Both `.txt` SQL captures were taken back-to-back against the same `PriceTicks` snapshot
(**404,625 rows** at capture time — see "Data volume" below) so the row counts and I/O
numbers are directly comparable; capturing them minutes apart, against a table whose
retention window had moved, was tried first and produced a mismatched row count between the
two queries (1200 rows in one, 1080 in the other) purely from clock drift — that false lead
is why both were re-captured in the same sitting.

### Data volume

404,625 rows is **not** two clean runs of the seed script (that would be 403,200 — 2 ×
201,600). It is 403,200 seeded rows plus ~1,425 rows written by `FakeTickService` (which
ticks every second for every reference ticker whenever the API process is up) during the
API sessions used for earlier end-to-end timing attempts before the seed script's own `PRINT`
reported 404,625. The seed script is not idempotent by design (see its header comment) — a
second run doesn't collide with the first run's timestamps, it appends another ~201,600
rows — so the exact total at any given moment depends on how many times it's been run and
how long the API has been up since. The number that matters for reproducing the SQL
captures above is "however many rows `seed-sparkline-history.sql`'s own `PRINT` reports
immediately before you capture," not a fixed constant.

## What was measured

**SQL Server statistics** — `SET STATISTICS TIME/IO ON` around the two query shapes above,
against the 404,625-row `PriceTicks` table, 20 tickers, 60-minute window (matching
`HistoryOptions.SparklineWindowMinutes`'s default).

**Harness divergence, stated up front:** `sparklines-n-plus-one.sql` replays v1's 20
single-ticker queries as a T-SQL `CURSOR` loop over *one* connection, because that's what's
expressible in a `.sql` script. The real v1 handler never did this — it opened 20 separate
ADO.NET connections and issued 20 separate round trips from the .NET process. The cursor's
own `OPEN`/`FETCH` bookkeeping (materializing and re-reading its result set) is measured
overhead the real loop never paid, and it shows up in the capture as extra `Tickers` and
`Worktable` logical reads that have nothing to do with the candle query itself — see the
corrected reading of those numbers below. `SET STATISTICS IO/TIME` therefore measures
*query cost* (how expensive is the SQL Server work), not the N+1's *real* cost, which is
round-trip count. The end-to-end HTTP section is what actually measures the round trips.

**End-to-end HTTP timing** — the real API (`dotnet run --project src/MarketPulse.Api`),
a freshly registered user, 20 watchlist tickers added via `POST
/api/v1/watchlist/items`, then 20 sequential `GET /api/v1/prices/sparklines` requests timed
with `curl -w '%{time_total}\n'` and the session cookie jar from registration. Both the
before and after runs were taken against the **same 404,625-row `PriceTicks` table** — the
before run was captured by temporarily reverting the three fix commits' files with `git
stash` (keeping the new test/docs files staged), running the reverted v1 loop against
today's data, then popping the stash and re-running against the identical database with no
reseed in between. An earlier attempt that reseeded between the before and after HTTP
captures produced a doubled data volume for the "after" run and made the fix look *slower*
end-to-end (67ms avg vs. 34ms) — a measurement artifact from an unfair comparison, not a real
regression; it is not reported below because it wasn't measuring the same thing twice.

## Results

### SQL Server statistics (`STATISTICS TIME`/`IO`, 20 tickers, 404,625 rows in `PriceTicks`)

**Query cost — the actual candle/sparkline `SELECT`s, excluding cursor harness bookkeeping:**

| | Before (N+1, 20 queries) | After (set-based, 1 query) |
|---|---|---|
| `PriceTicks` logical reads (sum) | 112 | 112 |
| `PriceTicks` scan count (sum) | 20 | 20 |
| `Tickers` logical reads (query itself) | 0 (the real per-ticker query never touches `Tickers`) | 2 (one scan, for the `selectedTickers` CTE) |
| `Worktable` logical reads (query itself) | 0 | 0 |
| **Query-cost logical reads total** | **112** | **114** |
| Rows returned | 1200 (20 tickers × 60 buckets) | 1200 (same) |

**Server-side logical reads for the query work are effectively unchanged (112 vs. 114).**
This is expected, not a wash that undermines the fix: 20 tickers means 20 indexed seeks
against the clustered `(Ticker, TimestampUtc)` index either way — one query issuing 20 seeks
internally costs SQL Server about the same as 20 queries issuing one seek each. Reducing
*that* number was never the fix's mechanism. The two extra `Tickers` reads in the after
column are the one-time cost of the `selectedTickers` CTE that drives the join; noise at
this scale.

**Batch cost:**

| | Before | After |
|---|---|---|
| Statement batches (`SQL Server Execution Times` blocks) | 69 (cursor `OPEN`/21×`FETCH`/`CLOSE` + 20 × query) | 4 |
| Ad-hoc parse & compile (one block per whole batch, not per statement) | 144 ms CPU / 144 ms elapsed | 11 ms CPU / 12 ms elapsed |
| Statement execution time, **excluding** parse & compile (sum) | 17 ms CPU / 6 ms elapsed | 24 ms CPU / 13 ms elapsed |

**Excluding the one-off ad-hoc compile, the set-based query is not faster server-side at
this data volume — it is slightly slower (13ms/24ms vs. 6ms/17ms).** The compile-time row
is not a fair before/after comparison either: each script compiles once, as a whole batch,
the first time SQL Server sees that exact text, so 144ms vs. 12ms says "these are two
different, unique ad-hoc batches" more than it says anything about the query shapes — it is
**not** 20 compiles collapsing to 1 (auto-parameterization already reused one cached plan
across all 20 cursor iterations; the loop only ever compiled its query text once). None of
this is a knock against the fix; it says the server-side cost of the SQL itself was never
where the N+1's damage was.

**The N+1's real, measured cost is round-trip count, not query cost: 20 independent
connections and commands collapsed to 1** — a number `STATISTICS IO`/`TIME` cannot show at
all, because both measure server-side work only and have no visibility into how many times
the client opened a connection or sent a command over the wire. That is exactly what the
end-to-end HTTP numbers below were captured to measure instead. (See "harness divergence"
above: the cursor script's own bookkeeping — the `Tickers`/`Worktable` reads that appeared
in an earlier draft of this table attributed to the fix's win — is overhead the *harness*
paid to fake a multi-connection loop inside one connection; the real C# v1 loop never paid
it, and it isn't part of either column above.)

### End-to-end HTTP timing (20 requests, `GET /api/v1/prices/sparklines`, 20-ticker watchlist, same 404,625-row `PriceTicks` table for both runs)

Raw captures: `sparklines-e2e-before.txt`, `sparklines-e2e-after.txt` (one `curl -w
'%{time_total}\n'` line per request, seconds).

| | Before (N+1) | After (set-based) |
|---|---|---|
| First request (cold, JIT/plan-cache warmup) | 111.0 ms | 89.9 ms |
| Remaining 19 requests — avg | 32.9 ms | 22.5 ms |
| Remaining 19 requests — min | 30.7 ms | 20.8 ms |
| Remaining 19 requests — max | 36.8 ms | 26.0 ms |
| All 20 requests — avg | 36.8 ms | 25.9 ms |

Steady-state (excluding the cold first request), the set-based fix is **~32% faster
end-to-end** (32.9ms → 22.5ms average, ~10.4ms saved per request) at 20 watchlist tickers —
and that gap widens with watchlist size, since the before column pays one full round trip
per ticker while the after column stays flat at one. At this data volume and tick cadence
the SQL-side cost of *either* shape is small in absolute terms (single-digit-to-double-digit
milliseconds); the query-count difference is what the fix actually buys, and it is the part
that would have kept costing more as retention and watchlist size grew, not the part that
happens to show up biggest in today's numbers.

## Reproducing

```bash
docker compose up -d sqlserver
# password: the dev SA password in src/MarketPulse.Api/appsettings.Development.json
docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P '<SA_PASSWORD>' -d MarketPulse -i /dev/stdin < docs/sql/seed-sparkline-history.sql
docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P '<SA_PASSWORD>' -d MarketPulse -i /dev/stdin < docs/sql/sparklines-n-plus-one.sql \
  > docs/sql/sparklines-before.txt
docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P '<SA_PASSWORD>' -d MarketPulse -i /dev/stdin < docs/sql/sparklines-set-based.sql \
  > docs/sql/sparklines-after.txt
```

For the end-to-end numbers: run the API, register a user, `POST` 20 tickers to
`/api/v1/watchlist/items` (keeping the cookie jar and the `X-CSRF-Token` header from
registration), then loop 20 `curl -s -o /dev/null -w '%{time_total}\n' -b <jar>
http://localhost:5100/api/v1/prices/sparklines`. To reproduce the before/after pair fairly
(same `PriceTicks` snapshot for both), capture the after run first against the fix as it
exists on disk, then `git stash push -- <the three fix files>` to temporarily restore the v1
loop, rebuild, capture the before run against the *same* database with no reseed in between,
and `git stash pop` to restore the fix. Reseeding — or running the API, which lets
`FakeTickService` add rows — between the two captures invalidates the comparison; see "Data
volume" above for what that looks like when it goes wrong.

## Two known deviations from a naive read of the query text

Both were caught by actually running the SQL against the container, not by inspection:

1. `PRINT CONCAT('...', (SELECT COUNT(*) FROM PriceTicks))` fails with SQL Server error 1046
   ("Subqueries are not allowed in this context") — `PRINT`/`CONCAT` cannot take a subquery
   as an argument. Fixed in `seed-sparkline-history.sql` by assigning the count to a
   `DECLARE`d variable first, then `PRINT`ing that.
2. `WITH tickers AS (SELECT TOP (20) Code FROM Tickers ...)` fails with error 252
   ("Recursive common table expression 'tickers' does not contain a top-level UNION ALL
   operator") — SQL Server's default case-insensitive collation makes the CTE alias
   `tickers` collide with the `Tickers` table it selects from, so `FROM Tickers` inside the
   CTE's own definition resolves to the CTE itself, and the engine reads that as a
   self-referencing recursive CTE. Fixed in `sparklines-set-based.sql` by renaming the CTE to
   `selectedTickers`.

## Data quirk worth naming

The `SparklinePoint` record used by `DapperPriceHistoryReader.GetSparklinesAsync` declares
`Bucket` as `int`, not `long`. Dapper's constructor-matching materialization requires an
exact type match against the result column, and `DATEDIFF(SECOND, ...)` returns SQL Server's
`int` (as the comment on `CandleSql` already noted: "holds until 2068") — a `long` there
throws `InvalidOperationException` at the first real query, not at compile time. Caught by
running `SparklineQueryCountTests` after the fix landed, not by review.
