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

Both `.txt` captures were taken back-to-back against the same `PriceTicks` snapshot
(**404,625 rows** — two runs of the seed script, 20 tickers × 7 days × 1/minute each) so the
row counts and I/O numbers are directly comparable; capturing them minutes apart, against a
table whose retention window had moved, was tried first and produced a mismatched row count
between the two queries (1200 rows in one, 1080 in the other) purely from clock drift — that
false lead is why both were re-captured in the same sitting.

## What was measured

**SQL Server statistics** — `SET STATISTICS TIME/IO ON` around the two query shapes above,
against the 404,625-row `PriceTicks` table, 20 tickers, 60-minute window (matching
`HistoryOptions.SparklineWindowMinutes`'s default).

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

| | Before (N+1, `sparklines-before.txt`) | After (set-based, `sparklines-after.txt`) |
|---|---|---|
| Queries issued | 20 (one per ticker, inside a cursor loop) | 1 |
| Statement batches (`SQL Server Execution Times` blocks) | 69 (cursor open/fetch/close + 20 × query) | 4 |
| `PriceTicks` logical reads (sum) | 112 | 112 |
| `PriceTicks` scan count (sum) | 20 | 20 |
| `Tickers` logical reads (sum) | 42 (scanned once per cursor `FETCH`, 21 times, plus the driving `SELECT`) | 2 (scanned once) |
| `Worktable` logical reads (sum) | 82 (a `ROW_NUMBER`/spool worktable per iteration) | 0 |
| **Total logical reads** | **236** | **114** (**52% fewer**) |
| Elapsed time (sum across all batches) | 150 ms | 25 ms (**6× faster**) |
| CPU time (sum across all batches) | 161 ms | 35 ms (**4.6× faster**) |
| Rows returned | 1200 (20 tickers × 60 buckets) | 1200 (same) |

`PriceTicks` itself gets scanned 20 times either way — 20 tickers means 20 indexed seeks
against the clustered `(Ticker, TimestampUtc)` index regardless of whether they run inside
one query or twenty, so that row is the same in both columns and is *not* where the win
lives. The win is everything the N+1 shape paid 20 times over that the set-based query pays
once: the `Tickers` lookup, the per-iteration `ROW_NUMBER` spool (`Worktable`), and — the
part these numbers under-state — 20 separate query compilations and 20 separate network
round trips to SQL Server, collapsed to 1. That last part is invisible to `STATISTICS
IO`/`TIME` (both measure server-side work only) and is exactly what the end-to-end numbers
below capture instead.

### End-to-end HTTP timing (20 requests, `GET /api/v1/prices/sparklines`, 20-ticker watchlist, same 404,625-row `PriceTicks` table for both runs)

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
http://localhost:5100/api/v1/prices/sparklines`.

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
