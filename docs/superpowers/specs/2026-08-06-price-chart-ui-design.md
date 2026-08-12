# Slice 7b — Price chart UI

**Date:** 2026-08-06
**Status:** Approved
**Slice:** 7b — the second half of roadmap slice 7 (*Price history and charts*, phase 2).
7a shipped the backend (candles + sparklines endpoints, ADR-006); this slice renders it and
closes phase 2.

## Why this slice exists

Slice 7a persists ticks and serves OHLC candles and watchlist sparklines, but nothing
renders them. Two README promises are still unbacked: "lazy-loaded charting" with
route-based code splitting, and `stale-while-revalidate` for chart history. This slice adds
the chart surface, the sparklines, the zod contracts for the 7a endpoints, and the lazy
route that keeps the chart library out of the main bundle.

### Decisions taken, with rejected alternatives

1. **lightweight-charts for the candle chart.** TradingView's canvas library: purpose-built
   candlestick rendering, ~45 KB gzipped, zero dependencies, Apache-2.0 (licence checked per
   the dependency policy). *Rejected:* Recharts (no native candlestick series — candles
   would be composed from custom SVG shapes at ~2× the bundle cost); hand-rolled SVG (a real
   chart — axes, scales, crosshair, resize — is a slice of work on its own, and the README
   explicitly promises a charting library). One honest note on the dependency policy's
   "more than one call site" rule: the library has exactly one call site (`CandleChart`).
   It earns its place anyway because it replaces a subsystem we would otherwise build, not a
   utility we could inline — that is the buy-vs-build exception, stated rather than smuggled.
2. **A dedicated lazy route, `/prices/:ticker`.** `React.lazy` + `Suspense` puts the chart
   screen — and with it `lightweight-charts` — in its own chunk, loaded only on navigation.
   This is the README's route-based code-splitting story, and the chunk split is *verified*
   during implementation by inspecting the build output, not assumed. *Rejected:* an
   expandable watchlist row (murky lazy boundary, watchlist screen grows a second job); a
   modal (no deep-linkable URL, a11y burden, no code-splitting story).
3. **SWR only — no live tick-merging into the open candle.** The chart shows fetched
   history; freshness comes from TanStack Query `staleTime`/`refetchInterval` scaled to the
   selected interval. *Rejected for this slice:* subscribing the chart to the SignalR stream
   and updating the in-progress candle (real work — client-side bucket alignment, coupling
   to `streamReducer` — that roughly doubles the slice; the dashboard already shows live
   prices). Deferred, not dismissed: it is the natural 7c/phase-2 follow-up if wanted.
4. **Sparklines are hand-rolled SVG, not the chart library.** A ~40-line polyline component
   keeps the watchlist screen free of the chart dependency entirely — loading a canvas
   charting engine to draw 25 tiny lines would defeat decision 2. One batch query feeds all
   rows (the endpoint 7a built specifically to avoid N+1 on this screen).

## Scope boundary

### In scope

- zod schemas + client methods for `GET /prices/{ticker}/candles` and
  `GET /prices/sparklines` in `packages/api-client`.
- `Sparkline` SVG component, `useSparklines` query, sparkline cell in the watchlist rows.
- `/prices/:ticker` lazy route: `PriceHistoryScreen` (interval switcher, states),
  `CandleChart` (lightweight-charts lifecycle), `useCandles` query.
- Watchlist ticker cell becomes a link; back navigation; a11y parity with existing screens.
- MSW handlers, unit/screen tests, one new Playwright journey.
- Build-output verification that `lightweight-charts` lands only in the lazy chunk.

### Out of scope

- Live tick-merging into the open candle (decision 3).
- Volume bars, multi-series, comparisons, drawing tools (the tick has no volume anyway).
- `rollup-plugin-visualizer` and the CI dependency budget — slice 12's performance pass.
- Any change to the 7a API or backend.
- Styling beyond the existing token system; no new design-system work.

## Architecture

### Contracts (packages/api-client)

`schemas.ts` gains, in the established declare-once/infer style:

```ts
export const candleSchema = z.object({
  t: z.string().datetime({ offset: true }),
  o: z.number(), h: z.number(), l: z.number(), c: z.number(),
});
export const candlesSchema = z.object({
  ticker: z.string(),
  interval: z.enum(['1m', '5m', '1h', '1d']),
  candles: z.array(candleSchema),
});
export const sparklinesSchema = z.object({
  sparklines: z.record(z.string(), z.array(z.number())),
});
```

`client.ts` gains `getCandles(ticker, interval, from, to, { signal })` and
`getSparklines({ signal })`, both `parse()`d at the boundary — a malformed response is
unrenderable, so the REST path fails loud, exactly like the watchlist calls. Numbers arrive
as JSON numbers; the backend serialises `decimal` as a number, and price magnitudes here are
far inside double precision — noted because money-as-float is otherwise a smell worth
flagging.

### Sparklines (watchlist screen)

- **`Sparkline`** (`packages/ui`): pure presentational SVG — normalised polyline over an
  array of numbers, fixed viewBox, stroke from a design token, `aria-hidden="true"` (the
  row's price cell already carries the accessible price; a trend line is decoration to a
  screen reader). No data (fewer than 2 points) → renders nothing.
- **`useSparklines`** (`features/prices`): one TanStack Query over `getSparklines`,
  `staleTime` 60 s, `refetchInterval` 60 s — the server buckets by minute, so faster polling
  buys nothing. Query key `['sparklines']`; cleared on logout by the existing
  `queryClient.clear()`.
- The watchlist row renders `<Sparkline points={sparklines[ticker]} />` beside the price
  cell. Query error → sparklines silently absent (the watchlist's own data is unaffected;
  a decoration must not degrade the screen it decorates).

### Chart route

- **Routing:** `const PriceHistoryScreen = lazy(() => import('./features/history/PriceHistoryScreen'))`,
  mounted under the existing `ProtectedRoute` at `/prices/:ticker`, wrapped in `Suspense`
  with a `role="status"` fallback. `lightweight-charts` is imported only inside this
  feature folder, so Vite splits it into the route chunk automatically — an implementation
  step runs `pnpm build` and verifies the main chunk does not contain the library.
- **`PriceHistoryScreen`:** reads `:ticker`, owns the selected interval
  (`useState<'1m'|'5m'|'1h'|'1d'>`, default `1m`), renders the switcher (buttons with
  `aria-pressed`), and the four states: loading (`role="status"`), error (`role="alert"` —
  a 404 `unknown-ticker` renders "Unknown ticker" with a link back to the watchlist),
  empty (`role="status"`, "No history in this range yet"), and the chart.
- **Window per interval** — sized against 7a's 1000-bucket cap and its 7-day retention,
  honestly: `1m` → last 6 h (360 buckets), `5m` → 24 h (288), `1h` → 7 d (168), `1d` → 7 d
  (≤ 7 candles — thin until retention grows, and the empty/sparse state says so rather than
  pretending). `from`/`to` are computed at query time and rounded down to the interval so
  the query key is stable within a refetch window.
- **`useCandles(ticker, interval)`:** query key `['candles', ticker, interval]`;
  `staleTime` 30 s and `refetchInterval` 60 s for `1m`/`5m`; `staleTime` 5 min, no
  auto-refetch for `1h`/`1d`. `retry: false` on 404 (an unknown ticker is not transient),
  default retry otherwise. This is the README's `stale-while-revalidate`: render cached
  candles instantly on revisit, refresh in the background.
- **`CandleChart`:** the only file that imports `lightweight-charts`. Owns the full
  lifecycle in effects — create chart + candlestick series on mount, `setData` when candles
  change, `ResizeObserver` for width, `chart.remove()` in cleanup — written to survive
  StrictMode's double-invoke like every other hook in the app (Q5.2 discipline). Colors
  from design tokens. Receives plain props (`candles`), holds no fetching logic.

### Navigation & a11y

The watchlist ticker cell becomes `<Link to={`/prices/${ticker}`} aria-label={`${ticker} price history`}>`.
The chart screen: `<section aria-labelledby>` with a real `<h2>` ("IVV — price history"),
a back link to `/`, interval buttons labelled and `aria-pressed`, and all states exposed
through roles — so RTL and Playwright both keep querying the accessibility tree, unchanged
from the house rules.

## Error surface

| Case | Behaviour |
|---|---|
| 404 `unknown-ticker` from candles | Error state with the server's slug branched on `ApiError.errorCode`, link back to watchlist, `retry: false` |
| Other candle errors (5xx, network) | `role="alert"` error state, TanStack Query default retries |
| Sparklines query fails | Sparklines absent; watchlist unaffected; no error surface (decoration) |
| Malformed response body | zod `parse()` throws → error state (REST path fails loud, per Q3.1) |
| Empty candles array | Explicit empty state, not a blank chart |

## Testing

- **Schemas:** parse/reject tests beside the existing schema tests in `packages/api-client`.
- **Client:** MSW-backed tests for both methods (URL shape, query params, zod boundary),
  in the established `client.test.ts` style with `onUnhandledRequest: 'error'`.
- **`Sparkline`:** render test — points produce a polyline, `<2` points render nothing.
- **`PriceHistoryScreen`:** RTL + MSW — loading→data, 404 path (`unknown-ticker` message +
  back link), empty state, interval switch triggers a new request. `lightweight-charts` is
  module-mocked (jsdom has no canvas); the mock records the candles handed to it, which is
  the screen's actual contract with the chart.
- **Watchlist:** existing tests extended — ticker cell is a link; sparkline renders from
  the MSW-stubbed batch endpoint.
- **Playwright:** one new journey (real canvas, real chunks): sign in → watchlist shows a
  sparkline → click a ticker → chart screen renders a canvas → switch interval → request
  observed. This is the only place the real library executes in CI.

### Deliberately not tested

- lightweight-charts' own rendering (Microsoft's-library rule: we don't test TradingView's
  canvas output; the Playwright journey proves it mounts and draws *something*).
- Bundle-size regression in CI — the chunk-split check is a one-time implementation
  verification; automated budgets arrive with slice 12.

## Done criteria

- `pnpm -r typecheck`, `pnpm -r test`, and the full backend suite green; build 0 warnings.
- `pnpm build` output shows `lightweight-charts` only in the lazy route chunk; main-bundle
  size recorded before/after in the task report.
- Every Playwright journey green — the existing suite (auth journeys plus 5b's portfolio
  journey) and the new chart journey.
- Manual check against the running stack: sparklines visible on the watchlist within two
  refetch windows of first data; chart renders for a seeded ticker on all four intervals;
  unknown-ticker URL shows the 404 state.
- ROADMAP updated in the closing commit: slice 7 complete, phase 2 **Done**.

## Interview category coverage

Advances category 5 hardest (lazy routes + Suspense, query-key design,
stale-while-revalidate policy, third-party-library lifecycle inside effects, StrictMode
survival), category 9 (code splitting made real and verified in the build output — the
first genuine entry in the README's bundle story), category 3 (two more boundary schemas in
the declare-once/infer pattern), and category 14 (module-mocking a canvas library honestly
while letting Playwright own the real rendering).

## What comes next

Phase 2 closes with this slice. The deferred live-candle merge is recorded in decision 3 as
the natural follow-up if the phase ever reopens; otherwise the roadmap's next stop is
slice 8 (observability).
