# Slice 5b — Portfolio dashboard: "The portfolio, live"

**Date:** 2026-08-05
**Depends on:** slice 5a (portfolio backend, merged), slice 3 (design system), slice 4b (client patterns: ADR-007, api-client conventions)
**Branch:** `feature/slice-5b-portfolio-dashboard` → `test`

---

## Why this slice exists

Slice 5a built a portfolio that cannot lose a transaction — and nothing on the screen shows
it. The API records trades idempotently, computes average cost and realised P&L, and pages
history; the dashboard still has one authenticated screen, the watchlist. This slice is the
product half: a `/portfolio` route where holdings carry **live unrealised P&L** derived from
the price stream, trades are recorded through a form that always sends an idempotency key,
and the history is visible. It closes phase 4's remainder in the roadmap and is the first
slice to exercise 5a's headline guarantees from the client side — the retrying client the
final 5a review said these endpoints were hardened for.

It is also ADR-007's third worked example: unrealised P&L is server truth (holdings, average
cost) combined with live data (the price stream) **at render time** — never stored, never
fetched, never a second state layer.

### Decisions taken, with rejected alternatives

| Decision | Chosen | Rejected because |
|---|---|---|
| Surface | **A `/portfolio` route with header navigation (`Watchlist \| Portfolio`), shown only when authenticated** | Tabs on `/` keep routing untouched but grow the dashboard into a tab-state container and make the portfolio un-linkable; stacking both products on one page interleaves their loading and error states. A route gives three sections room and gives Playwright an address. This is the app's first in-app navigation — `NavLink` with `aria-current` is the whole mechanism |
| Unrealised P&L | **Derived at render: `units × (livePrice − averageCost)`, from the `['portfolio']` cache and the existing price stream** | Fetching it from the server was rejected in 5a (the server holds no current price). Storing the derivation in state (or a store) is the exact mistake ADR-007 exists to forbid. Prices are public broadcast — `usePriceStream` already receives every ticker, held or not, so no new subscription surface is needed |
| Missing live price | **Em-dash, not zero** | A holding whose ticker hasn't ticked yet has *unknown* unrealised P&L. Zero is a lie with a sign; the em-dash is the same honesty `PriceCell` already practises before its first tick |
| Idempotency key lifecycle | **One `crypto.randomUUID()` per user submission, generated at submit time; the same key travels with any retry of that submission; the next deliberate submission gets a fresh key** | Key-per-request turns the mechanism off (every retry re-executes). Key-per-form-mount replays a *second deliberate trade* as the first — silently swallowing a real order because the user happened not to remount. Submission-scoped is what the server's semantics were designed for: double-click and network resend replay; new intent gets a new key |
| Ticker entry | **Free-text input, server-side `unknown-ticker` surfaced inline** | A select over known tickers needs a tickers endpoint that does not exist, and building one is backend scope this slice does not have. Free-text is exactly the watchlist add-form's contract, and the validation already exists server-side with a named error code |
| Trade price | **User-typed fill price, no prefill from the live stream** | 5a settled that a trade records the user's fill. Prefilling from the stream invites recording a price the user never traded at, and adds a live dependency to a form whose e2e determinism comes precisely from user-supplied prices |
| History paging | **Newest-first list with a "Load more" button appending the next `skip` page** | Cursor or page-number pagination is an abstraction the notifications panel already declined at the same scale. Load-more is one `useState` and one button |

---

## Scope boundary

### In scope

| Layer | Deliverable |
|---|---|
| `packages/api-client` | `holdingSchema`, `portfolioSchema` (holdings + `totalRealisedPnL`), `transactionSchema`; methods `getPortfolio(signal?)`, `getTransactions(skip, take, signal?)`, `recordTransaction(trade, idempotencyKey, signal?)` — the key is an explicit parameter set as the `Idempotency-Key` header |
| `apps/dashboard` | `features/portfolio/`: `usePortfolio` + `useRecordTransaction` + `useTransactions` hooks; `PortfolioScreen` (three sections); `HoldingsTable` (live unrealised P&L via `usePriceStream`); `TradeForm`; `TransactionHistory` with load-more. Header `Nav` component (`Watchlist \| Portfolio`, authenticated only); `/portfolio` route under `ProtectedRoute` in `App.tsx` |
| e2e | `portfolio.spec.ts`: register → navigate → buy → holdings row (units, average cost) → sell higher → realised P&L positive, units reduced → history newest-first → reload persists |
| Docs | README/ROADMAP rows this slice closes; `TESTING.md`; one-paragraph ADR-007 addendum naming unrealised P&L as the third worked example |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

Any backend change (no new endpoints, no tickers list, no DTO changes) · portfolio notes
(its own slice) · charts, sparklines, or allocation visuals · CSV export · price prefill in
the trade form · editing or deleting trades (the API is append-only) · sell-from-row
shortcuts prefilled into the form · optimistic updates on trade recording (a trade is money;
the UI waits for the server's answer) · a tickers autocomplete.

---

## Architecture

### Navigation

`App.tsx` gains a `Nav` beside the wordmark: two `NavLink`s rendered only when `useSession`
has a session (the same gate `NotificationBell` uses). `aria-current="page"` styling via the
`NavLink` active state and tokens; no breadcrumbs, no menu. Routes: `/` (watchlist,
unchanged) and `/portfolio`, both under `ProtectedRoute`.

### Data flow

- `usePortfolio` — `['portfolio']` query over `getPortfolio`.
- `useTransactions` — `['transactions']` query; load-more keeps an accumulated list keyed by
  `skip` pages (append on fetch of the next page; the newest-first invariant comes from the
  server). A trade invalidation resets history to its first page — the new trade is by
  definition at the top, and reconciling a fresh head against stale deeper pages is
  complexity with no reader.

  *Amended 2026-08-05, during implementation.* The first cut of this hook implemented
  "keeps an accumulated list keyed by pages" as a single query whose `take` grew with each
  load-more (`take = pages × pageSize`, `skip` fixed at 0). That window hits
  `GetTransactionsQuery`'s server-side cap (`Take ∈ [1,100]`) at the sixth default-sized
  load-more — `take=120` — which the server 400s. The failure was silent: `keepPreviousData`
  kept the old rows on screen, the resulting short/failed page read as "history exhausted"
  rather than "request rejected", and rows past 100 became permanently unreachable with no
  error shown. The fix is `useTransactions` on `useInfiniteQuery`: each page is its own
  request at `skip = itemsFetchedSoFar, take = pageSize`, so `take` never grows past
  `pageSize` regardless of how many pages are loaded. The hook now also returns `isError`,
  which it did not before — the same silent-failure bug independently let a failed *initial*
  fetch render as `TransactionHistory`'s empty state ("No trades recorded yet.") rather than
  an error, which the `isError` addition and the component's new error branch both close. See
  `docs/TESTING.md` for the cap-safety test this proved out.
- `useRecordTransaction` — mutation calling `recordTransaction(trade, key)`; on success
  invalidates `['portfolio']` **and** `['transactions']`. The 201 body carries the updated
  portfolio and could seed `setQueryData`, but one invalidation path was chosen over two
  update paths — the alerts mutations' precedent. No optimistic update, deliberately: a
  trade is money, and the UI shows nothing the server has not confirmed.
- The key is generated inside the form's submit handler (`crypto.randomUUID()`), held in a
  ref for the lifetime of that submission (so a retry button reuses it), and discarded on
  success or on starting a new submission.
- Unrealised P&L: `HoldingsTable` combines `usePortfolio` holdings with `usePriceStream`
  prices per row. `unrealised = units × (live − averageCost)`; `—` while `live` is
  undefined. Total unrealised at the foot follows the same rule (em-dash if any held ticker
  lacks a price; partial sums lie).

### Error surface

- 400 (`unknown-ticker`, `invalid-side`, `invalid-units`, `invalid-price`) and 422
  (`insufficient-holdings`, `idempotency-key-reuse`) → the ProblemDetails detail inline via
  the `Alert` primitive under the form, the AlertCell/auth pattern.
- 409 (`concurrent-update`, `idempotency-in-flight`) → inline message with a **Retry**
  button that resubmits with the *same* key — this is the 5a server contract exercised as
  designed (a replay or the winner's completion, never a duplicate).
- The form's submit button disables while pending. Native constraint validation (`required`,
  `min`, `step`) on units and price, the AlertCell precedent — jsdom-guarded in tests the
  same way.

  *Amended 2026-08-05, during implementation.* Retry was originally gated on `status === 409`
  alone. A network failure (the `fetch` promise itself rejecting — no response, so no status)
  is the *other* ambiguous outcome the idempotency key exists for: the client never learned
  whether the request landed. Gating Retry on 409 only left the user's sole recourse to a new
  submission with a fresh key, which duplicates the trade if the original had in fact
  committed. The rule is now: retry with the *same* key whenever the outcome is unknown or
  explicitly retryable — 409, or any error with no HTTP status. A definitive 4xx/5xx-with-
  status (`unknown-ticker`, `insufficient-holdings`, etc.) stays non-retryable, since
  resending it would just repeat the same rejection.

## Testing

| Suite | What |
|---|---|
| Vitest (`packages/api-client`) | Schemas parse 5a's wire shapes; `recordTransaction` sets the `Idempotency-Key` header and CSRF header; URL/verb assertions in the existing fetch-mock pattern |
| Vitest (`apps/dashboard`) | `usePortfolio`/`useTransactions` (msw); `useRecordTransaction` invalidations; **key lifecycle**: retry after 409 reuses the same key, a subsequent submission generates a new one (assert via captured request headers); `HoldingsTable` derivation (positive/negative unrealised, em-dash without a price, footer totals incl. the any-missing-price em-dash rule); `TradeForm` (posts exact body + header; 400/422 detail surfaced; 409 shows Retry; pending disables submit); `TransactionHistory` load-more appends the next page; `Nav` renders only with a session and marks the active route |
| Playwright | `portfolio.spec.ts` as scoped above. Deterministic by construction: user-typed prices decide every assertion except the live-price cell, which reuses the watchlist journey's 15s-tolerant first-tick assertion |

### Deliberately not tested

Concurrent-sell 409s end-to-end in the browser (the anomaly pair and middleware fact own
that path) · idempotency replay at the HTTP layer (5a's integration facts own it; the client
tests assert the *key discipline*, which is the part 5a cannot see) · load/perf of history
paging.

---

## Done criteria

1. All existing suites green; new Vitest and Playwright tests green.
2. The Playwright journey passes: a buy and a higher sell recorded through the browser
   produce the correct units, average cost, and positive realised P&L on screen, history
   lists both newest-first, and everything survives a reload.
3. Every `POST /portfolio/transactions` the dashboard sends carries an `Idempotency-Key`,
   asserted in tests; retry-after-409 reuses the key; a new submission does not.
4. Unrealised P&L appears within a tick of prices flowing and is never fetched or stored —
   verified by the absence of any such field in the api-client schemas and by the derivation
   tests.
5. README/ROADMAP no longer list the portfolio UI as absent; TESTING.md and the ADR-007
   addendum land; phase 4 closes in the roadmap.

---

## Interview category coverage

| # | Category | What this slice banks |
|---|---|---|
| 5 | **Frontend framework depth** ⭐ | Render-time derivation over two live sources (query cache × websocket stream); submission-scoped idempotency keys as client discipline; first routing/navigation story |
| 7 | **Web API design** | The consumer's half of idempotency: key lifecycle, retry semantics against 409s, replay expectations — the part the server-side slice couldn't demonstrate |
| 14 | **Testing** | Asserting key discipline via captured headers; deterministic e2e over user-supplied prices; the em-dash derivation edge cases |

---

## What comes next

Phase 4 (frontend core) closes with this slice. The roadmap's remaining large stories:
slice 6 (real market-data ingestion, where the in-memory rule cache 4a named becomes
relevant), the observability slice (which inherits the unbounded-requeue and idempotency
retention/orphaned-claim items), and portfolio notes (the README's DOMPurify story).
