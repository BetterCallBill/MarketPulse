# Slice 7b — Price Chart UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render slice 7a's price history — hand-rolled SVG sparklines on the watchlist and a lazy-loaded `/prices/:ticker` candle-chart route using lightweight-charts — with zod contracts and stale-while-revalidate via TanStack Query.

**Architecture:** Contracts live in `packages/api-client` (declare-once zod, types inferred). `Sparkline` is a pure SVG component in `packages/ui`; the chart screen and everything importing `lightweight-charts` live in a new `features/history` folder loaded via `React.lazy`, so the library lands only in the route chunk (verified against the build output). Data flows through two TanStack Query hooks with interval-scaled freshness.

**Tech Stack:** React 18.3, React Router 7, TanStack Query 5, zod 3, lightweight-charts 5 (new), Vitest 2 + RTL + MSW 2, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-06-price-chart-ui-design.md` — binding; read it first.

## Global Constraints

- Backend untouched: no file under `src/` or `tests/MarketPulse.*` changes in this slice.
- `pnpm -r typecheck` and `pnpm -r test` green after every task; TypeScript is strict with `noUncheckedIndexedAccess` — index/record access yields `T | undefined`, handle it.
- Conventional commits, one change each, **no Co-Authored-By trailer** (repo rule).
- TDD per task: failing test → implementation → green → commit.
- Branch: all work on `feature/slice-7b-price-chart-ui`, merged to `test` only after the full gate.
- Tests query through the accessibility tree (`getByRole`/`getByLabelText`); MSW runs with `onUnhandledRequest: 'error'`; every acquired resource is released in effect cleanup and survives StrictMode double-invoke.
- Colour in components comes only from semantic tokens (`--mp-accent`, `--mp-price-up`, `--mp-price-down`, …) — `noPrimitiveLeak.test.ts` enforces this in `packages/ui`.
- `lightweight-charts` may be imported ONLY inside `apps/dashboard/src/features/history/` (the lazy chunk boundary).

## File Structure

```
packages/api-client/src/
  schemas.ts                  (modify — candle/sparkline schemas + types)
  client.ts                   (modify — getCandles, getSparklines)
  schemas.test.ts             (modify — new schema tests)
  client.test.ts              (modify — new endpoint tests)
packages/ui/src/
  components/Sparkline/       (new — Sparkline.tsx, .module.css, .test.tsx)
  index.ts                    (modify — export)
apps/dashboard/
  package.json                (modify — lightweight-charts)
  src/features/prices/useSparklines.ts        (new)
  src/features/watchlist/WatchlistScreen.tsx  (modify — ticker link + Trend column)
  src/features/watchlist/WatchlistScreen.test.tsx (modify)
  src/features/history/useCandles.ts          (new)
  src/features/history/CandleChart.tsx        (new — the ONLY lightweight-charts importer)
  src/features/history/CandleChart.module.css (new)
  src/features/history/CandleChart.test.tsx   (new)
  src/features/history/PriceHistoryScreen.tsx (new — default export for React.lazy)
  src/features/history/PriceHistoryScreen.module.css (new)
  src/features/history/PriceHistoryScreen.test.tsx   (new)
  src/App.tsx                 (modify — lazy route)
  src/test/setup.ts           (modify — ResizeObserver stub)
tests/e2e/specs/chart.spec.ts (new)
docs/ROADMAP.md               (modify — closing commit)
```

---

### Task 1: Contracts — zod schemas and client methods

**Files:**
- Modify: `packages/api-client/src/schemas.ts`
- Modify: `packages/api-client/src/client.ts`
- Modify: `packages/api-client/src/index.ts` (only if it re-exports names explicitly — check; it likely `export *`)
- Test: `packages/api-client/src/schemas.test.ts`, `packages/api-client/src/client.test.ts`

**Interfaces:**
- Produces: `candleIntervalSchema`/`CandleInterval` (`'1m'|'5m'|'1h'|'1d'`), `candleSchema`/`Candle` (`{t,o,h,l,c}`), `candlesSchema`/`Candles` (`{ticker, interval, candles}`), `sparklinesSchema`/`Sparklines` (`{sparklines: Record<string, number[]>}`), and client methods `getCandles(ticker: string, interval: CandleInterval, from: string, to: string, signal?: AbortSignal): Promise<Candles>` and `getSparklines(signal?: AbortSignal): Promise<Sparklines>`.

- [ ] **Step 1: Create the branch**

```bash
git checkout test && git pull && git checkout -b feature/slice-7b-price-chart-ui
```

- [ ] **Step 2: Write the failing schema tests**

Append to `packages/api-client/src/schemas.test.ts`, following the file's existing style:

```ts
describe('candlesSchema', () => {
  it('parses a valid candles payload', () => {
    const parsed = candlesSchema.parse({
      ticker: 'IVV',
      interval: '1m',
      candles: [{ t: '2026-08-06T10:00:00+00:00', o: 10, h: 12, l: 9, c: 11 }],
    });
    expect(parsed.candles[0]?.c).toBe(11);
  });

  it('rejects an unknown interval', () => {
    expect(() =>
      candlesSchema.parse({ ticker: 'IVV', interval: '42s', candles: [] }),
    ).toThrow();
  });

  it('rejects a candle with a non-numeric price', () => {
    expect(() =>
      candlesSchema.parse({
        ticker: 'IVV',
        interval: '1m',
        candles: [{ t: '2026-08-06T10:00:00+00:00', o: 'ten', h: 12, l: 9, c: 11 }],
      }),
    ).toThrow();
  });
});

describe('sparklinesSchema', () => {
  it('parses a ticker-to-closes record, including empty arrays', () => {
    const parsed = sparklinesSchema.parse({ sparklines: { IVV: [1.5, 2.5], NDQ: [] } });
    expect(parsed.sparklines['IVV']).toEqual([1.5, 2.5]);
    expect(parsed.sparklines['NDQ']).toEqual([]);
  });

  it('rejects non-numeric closes', () => {
    expect(() => sparklinesSchema.parse({ sparklines: { IVV: ['high'] } })).toThrow();
  });
});
```

(Import `candlesSchema`, `sparklinesSchema` alongside the file's existing imports.)

- [ ] **Step 3: Run to verify they fail**

```bash
pnpm --filter @marketpulse/api-client test
```

Expected: FAIL — `candlesSchema` is not exported.

- [ ] **Step 4: Implement the schemas**

Append to `packages/api-client/src/schemas.ts` (before the type-export block), matching the file's declare-then-infer layout:

```ts
export const candleIntervalSchema = z.enum(['1m', '5m', '1h', '1d']);

export const candleSchema = z.object({
  t: z.string(),
  o: z.number(),
  h: z.number(),
  l: z.number(),
  c: z.number(),
});

export const candlesSchema = z.object({
  ticker: z.string().min(1).max(8),
  interval: candleIntervalSchema,
  candles: z.array(candleSchema),
});

export const sparklinesSchema = z.object({
  sparklines: z.record(z.string(), z.array(z.number())),
});
```

And to the type-export block:

```ts
export type CandleInterval = z.infer<typeof candleIntervalSchema>;
export type Candle = z.infer<typeof candleSchema>;
export type Candles = z.infer<typeof candlesSchema>;
export type Sparklines = z.infer<typeof sparklinesSchema>;
```

(`t` is `z.string()`, not `.datetime()` — the existing schemas treat timestamps as plain strings (`addedUtc`, `timestampUtc`); follow the house pattern rather than introducing a stricter one-off.)

- [ ] **Step 5: Write the failing client tests**

Append to `packages/api-client/src/client.test.ts`, following its MSW style (the file already has `setupServer` with `onUnhandledRequest: 'error'` and a base-URL constant — reuse them):

```ts
describe('price history', () => {
  it('getCandles hits the right URL and parses the response', async () => {
    let seenUrl = '';
    server.use(
      http.get(`${BASE}/api/v1/prices/IVV/candles`, ({ request }) => {
        seenUrl = request.url;
        return HttpResponse.json({
          ticker: 'IVV',
          interval: '5m',
          candles: [{ t: '2026-08-06T10:00:00+00:00', o: 10, h: 12, l: 9, c: 11 }],
        });
      }),
    );

    const result = await client.getCandles(
      'IVV', '5m', '2026-08-06T04:00:00.000Z', '2026-08-06T10:00:00.000Z',
    );

    expect(result.candles).toHaveLength(1);
    const url = new URL(seenUrl);
    expect(url.searchParams.get('interval')).toBe('5m');
    expect(url.searchParams.get('from')).toBe('2026-08-06T04:00:00.000Z');
    expect(url.searchParams.get('to')).toBe('2026-08-06T10:00:00.000Z');
  });

  it('getCandles surfaces the unknown-ticker slug as ApiError', async () => {
    server.use(
      http.get(`${BASE}/api/v1/prices/ZZZZ/candles`, () =>
        HttpResponse.json(
          { title: 'unknown-ticker', status: 404, detail: "Ticker 'ZZZZ' is not a known instrument." },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );

    await expect(
      client.getCandles('ZZZZ', '1m', '2026-08-06T04:00:00.000Z', '2026-08-06T10:00:00.000Z'),
    ).rejects.toMatchObject({ status: 404, errorCode: 'unknown-ticker' });
  });

  it('getSparklines parses the batch payload', async () => {
    server.use(
      http.get(`${BASE}/api/v1/prices/sparklines`, () =>
        HttpResponse.json({ sparklines: { IVV: [101, 102] } }),
      ),
    );

    const result = await client.getSparklines();

    expect(result.sparklines['IVV']).toEqual([101, 102]);
  });
});
```

(Adapt `BASE`/`client` identifiers to whatever the file actually names them — read the top of the file first. If existing tests build clients per-test, follow that instead.)

- [ ] **Step 6: Run to verify they fail** (methods don't exist)

```bash
pnpm --filter @marketpulse/api-client test
```

- [ ] **Step 7: Implement the client methods**

In `packages/api-client/src/client.ts`: add `candlesSchema`, `sparklinesSchema`, `type Candles`, `type Sparklines`, `type CandleInterval` to the schema imports, and add to the returned object beside `getPortfolio`:

```ts
getCandles: (
  ticker: string,
  interval: CandleInterval,
  from: string,
  to: string,
  signal?: AbortSignal,
): Promise<Candles> =>
  request(
    `/api/v1/prices/${encodeURIComponent(ticker)}/candles?interval=${interval}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`,
    { method: 'GET', signal },
    (d) => candlesSchema.parse(d),
  ),

getSparklines: (signal?: AbortSignal): Promise<Sparklines> =>
  request('/api/v1/prices/sparklines', { method: 'GET', signal }, (d) =>
    sparklinesSchema.parse(d),
  ),
```

- [ ] **Step 8: Run to verify green, then typecheck the workspace**

```bash
pnpm --filter @marketpulse/api-client test && pnpm -r typecheck
```

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "feat(api-client): candle and sparkline contracts for the 7a endpoints"
```

---

### Task 2: Sparkline component (packages/ui)

**Files:**
- Create: `packages/ui/src/components/Sparkline/Sparkline.tsx`
- Create: `packages/ui/src/components/Sparkline/Sparkline.module.css`
- Create: `packages/ui/src/components/Sparkline/Sparkline.test.tsx`
- Modify: `packages/ui/src/index.ts`

**Interfaces:**
- Produces: `Sparkline({ points: number[], width?: number, height?: number })` — renders `null` for fewer than 2 points; `aria-hidden` SVG polyline; exported from `@marketpulse/ui`.

- [ ] **Step 1: Write the failing tests**

`packages/ui/src/components/Sparkline/Sparkline.test.tsx` (follow the style of `StatusDot.test.tsx`):

```tsx
import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Sparkline } from './Sparkline';

describe('Sparkline', () => {
  it('renders a polyline for two or more points', () => {
    const { container } = render(<Sparkline points={[1, 3, 2]} />);
    const polyline = container.querySelector('polyline');
    expect(polyline).not.toBeNull();
    expect(polyline?.getAttribute('points')).toBeTruthy();
  });

  it('renders nothing for fewer than two points', () => {
    const { container } = render(<Sparkline points={[5]} />);
    expect(container.firstChild).toBeNull();
  });

  it('is hidden from assistive technology', () => {
    const { container } = render(<Sparkline points={[1, 2]} />);
    expect(container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('handles a flat series without dividing by zero', () => {
    const { container } = render(<Sparkline points={[7, 7, 7]} />);
    expect(container.querySelector('polyline')?.getAttribute('points')).not.toContain('NaN');
  });
});
```

- [ ] **Step 2: Run to verify they fail**

```bash
pnpm --filter @marketpulse/ui test
```

- [ ] **Step 3: Implement**

`Sparkline.tsx`:

```tsx
import styles from './Sparkline.module.css';

export interface SparklineProps {
  points: number[];
  width?: number;
  height?: number;
}

/**
 * A trend decoration, not a chart: no axes, no interaction, hidden from assistive
 * technology (the row's price cell already carries the accessible price). Fewer than two
 * points cannot describe a line, so nothing renders.
 */
export function Sparkline({ points, width = 96, height = 24 }: SparklineProps) {
  if (points.length < 2) return null;

  const min = Math.min(...points);
  const max = Math.max(...points);
  const range = max - min || 1; // flat series draws a flat line, not NaN
  const step = width / (points.length - 1);

  const path = points
    .map((p, i) => `${(i * step).toFixed(2)},${(height - ((p - min) / range) * height).toFixed(2)}`)
    .join(' ');

  return (
    <svg
      className={styles.sparkline}
      viewBox={`0 0 ${width} ${height}`}
      width={width}
      height={height}
      aria-hidden="true"
      focusable="false"
    >
      <polyline points={path} fill="none" />
    </svg>
  );
}
```

`Sparkline.module.css` (semantic tokens only — `noPrimitiveLeak.test.ts` will fail on a `--mp-grey-*` or hex here):

```css
.sparkline {
  display: block;
}

.sparkline polyline {
  stroke: var(--mp-accent);
  stroke-width: 1.5;
  stroke-linejoin: round;
  stroke-linecap: round;
}
```

`packages/ui/src/index.ts` — add alphabetically:

```ts
export { Sparkline, type SparklineProps } from './components/Sparkline/Sparkline';
```

- [ ] **Step 4: Run to verify green (includes the token-leak test)**

```bash
pnpm --filter @marketpulse/ui test
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(ui): Sparkline — hand-rolled SVG trend decoration"
```

---

### Task 3: Watchlist integration — useSparklines, ticker link, Trend column

**Files:**
- Create: `apps/dashboard/src/features/prices/useSparklines.ts`
- Modify: `apps/dashboard/src/features/watchlist/WatchlistScreen.tsx`
- Modify: `apps/dashboard/src/features/watchlist/WatchlistScreen.module.css`
- Test: `apps/dashboard/src/features/watchlist/WatchlistScreen.test.tsx`

**Interfaces:**
- Consumes: `apiClient.getSparklines` (Task 1), `Sparkline` (Task 2).
- Produces: `useSparklines(): UseQueryResult<Sparklines>` with key `['sparklines']`; watchlist ticker cell is a `<Link to={'/prices/' + ticker} aria-label="{ticker} price history">`; a Trend column renders sparklines. Task 6's route completes the link's destination.

- [ ] **Step 1: Write the failing tests**

In `WatchlistScreen.test.tsx`: first read `renderScreen` — screen tests since 5b mount inside a router; if not, wrap the render in `<MemoryRouter>`. Add an MSW handler to the server's base handlers so every existing test keeps passing under `onUnhandledRequest: 'error'`:

```ts
http.get('http://localhost:5100/api/v1/prices/sparklines', () =>
  HttpResponse.json({ sparklines: { IVV: [100.5, 101.25, 100.75] } }),
),
```

New tests:

```tsx
it('links the ticker to its price-history page', async () => {
  renderScreen();

  const link = await screen.findByRole('link', { name: 'IVV price history' });
  expect(link).toHaveAttribute('href', '/prices/IVV');
});

it('renders a sparkline from the batch endpoint', async () => {
  const { container } = renderScreen();

  await waitFor(() => expect(container.querySelector('polyline')).not.toBeNull());
});
```

- [ ] **Step 2: Run to verify they fail**

```bash
pnpm --filter @marketpulse/dashboard test -- WatchlistScreen
```

- [ ] **Step 3: Implement the hook**

`apps/dashboard/src/features/prices/useSparklines.ts`:

```ts
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../../api';

const sparklinesKey = ['sparklines'] as const;

/**
 * One batch query feeds every row — the endpoint 7a built specifically so this screen
 * never fans out per ticker. The server buckets by minute; polling faster buys nothing.
 */
export function useSparklines() {
  return useQuery({
    queryKey: sparklinesKey,
    queryFn: ({ signal }) => apiClient.getSparklines(signal),
    staleTime: 60_000,
    refetchInterval: 60_000,
  });
}
```

- [ ] **Step 4: Wire into the screen**

In `WatchlistScreen.tsx`:

```tsx
import { Link } from 'react-router-dom';
import { Sparkline } from '@marketpulse/ui';
import { useSparklines } from '../prices/useSparklines';
```

Inside the component: `const sparklines = useSparklines().data?.sparklines ?? {};`

Header row — add after the Last column:

```tsx
<th scope="col">Trend</th>
```

Ticker cell becomes:

```tsx
<td className={styles.ticker}>
  <Link
    className={styles.tickerLink}
    to={`/prices/${item.ticker}`}
    aria-label={`${item.ticker} price history`}
  >
    {item.ticker}
  </Link>
</td>
```

New cell after the price cell (a decoration: query failure or missing data renders nothing, never an error):

```tsx
<td>
  <Sparkline points={sparklines[item.ticker] ?? []} />
</td>
```

`WatchlistScreen.module.css` — add:

```css
.tickerLink {
  color: inherit;
  text-decoration: none;
}

.tickerLink:hover {
  text-decoration: underline;
}
```

- [ ] **Step 5: Run the dashboard suite — new tests green, existing tests still green**

```bash
pnpm --filter @marketpulse/dashboard test
```

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(dashboard): watchlist sparklines and ticker links to price history"
```

---

### Task 4: lightweight-charts + CandleChart

**Files:**
- Modify: `apps/dashboard/package.json` (via pnpm)
- Modify: `apps/dashboard/src/test/setup.ts` (ResizeObserver stub)
- Create: `apps/dashboard/src/features/history/CandleChart.tsx`
- Create: `apps/dashboard/src/features/history/CandleChart.module.css`
- Test: `apps/dashboard/src/features/history/CandleChart.test.tsx`

**Interfaces:**
- Produces: `CandleChart({ candles: Candle[] })` — the ONLY file importing `lightweight-charts`. Task 5's screen renders it; tests module-mock it.

- [ ] **Step 1: Add the dependency**

```bash
pnpm --filter @marketpulse/dashboard add lightweight-charts
pnpm ls --filter @marketpulse/dashboard lightweight-charts
```

Record the installed version in your report. The code below uses the **v5 API**
(`chart.addSeries(CandlestickSeries, opts)`). If the installed major is 4, use
`chart.addCandlestickSeries(opts)` instead and note the substitution.

- [ ] **Step 2: Stub ResizeObserver for jsdom**

Append to `apps/dashboard/src/test/setup.ts` (skip if already present — check first):

```ts
// jsdom has no ResizeObserver; the chart component observes its container.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver;
```

- [ ] **Step 3: Write the failing tests**

`CandleChart.test.tsx` — the mock is the contract: what data reaches the library, and that the lifecycle survives StrictMode:

```tsx
import { render } from '@testing-library/react';
import { StrictMode } from 'react';
import { describe, expect, it, vi } from 'vitest';

const setData = vi.fn();
const remove = vi.fn();
const applyOptions = vi.fn();
const fitContent = vi.fn();
const createChart = vi.fn(() => ({
  addSeries: vi.fn(() => ({ setData })),
  applyOptions,
  remove,
  timeScale: () => ({ fitContent }),
}));

vi.mock('lightweight-charts', () => ({
  createChart: (...args: unknown[]) => createChart(...args),
  CandlestickSeries: Symbol('CandlestickSeries'),
}));

import { CandleChart } from './CandleChart';

const CANDLES = [
  { t: '2026-08-06T10:00:00+00:00', o: 10, h: 12, l: 9, c: 11 },
  { t: '2026-08-06T10:01:00+00:00', o: 11, h: 13, l: 10, c: 12 },
];

describe('CandleChart', () => {
  it('hands converted candles to the series', () => {
    render(<CandleChart candles={CANDLES} />);

    expect(setData).toHaveBeenCalledWith([
      { time: Date.parse(CANDLES[0]!.t) / 1000, open: 10, high: 12, low: 9, close: 11 },
      { time: Date.parse(CANDLES[1]!.t) / 1000, open: 11, high: 13, low: 10, close: 12 },
    ]);
  });

  it('destroys the chart on unmount', () => {
    const { unmount } = render(<CandleChart candles={CANDLES} />);
    unmount();
    expect(remove).toHaveBeenCalled();
  });

  it('survives StrictMode double-invoke without leaking charts', () => {
    createChart.mockClear();
    remove.mockClear();
    const { unmount } = render(
      <StrictMode>
        <CandleChart candles={CANDLES} />
      </StrictMode>,
    );
    unmount();
    // however many charts were created, the same number were removed
    expect(remove).toHaveBeenCalledTimes(createChart.mock.calls.length);
  });
});
```

- [ ] **Step 4: Run to verify they fail** (module doesn't exist)

```bash
pnpm --filter @marketpulse/dashboard test -- CandleChart
```

- [ ] **Step 5: Implement**

`CandleChart.tsx`:

```tsx
import type { Candle } from '@marketpulse/api-client';
import {
  CandlestickSeries,
  createChart,
  type IChartApi,
  type ISeriesApi,
  type UTCTimestamp,
} from 'lightweight-charts';
import { useEffect, useRef } from 'react';
import styles from './CandleChart.module.css';

export interface CandleChartProps {
  candles: Candle[];
}

function toSeriesData(candles: Candle[]) {
  return candles.map((c) => ({
    time: (Date.parse(c.t) / 1000) as UTCTimestamp,
    open: c.o,
    high: c.h,
    low: c.l,
    close: c.c,
  }));
}

/**
 * The only file that imports lightweight-charts — it lives in the lazy route chunk and
 * nowhere else. Owns the full chart lifecycle in effects: create on mount, setData on
 * change, resize with the container, remove on cleanup. Written to survive StrictMode's
 * deliberate double-invoke: each mount creates its own chart and each cleanup removes it.
 */
export function CandleChart({ candles }: CandleChartProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const seriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null);
  const chartRef = useRef<IChartApi | null>(null);
  const candlesRef = useRef(candles);
  candlesRef.current = candles;

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    // Canvas cannot read CSS custom properties; resolve the semantic tokens here so the
    // chart still follows the design system.
    const tokens = getComputedStyle(container);
    const up = tokens.getPropertyValue('--mp-price-up').trim() || '#4ade80';
    const down = tokens.getPropertyValue('--mp-price-down').trim() || '#f87171';
    const text = tokens.getPropertyValue('--mp-text-secondary').trim() || '#8b95a5';

    const chart = createChart(container, {
      height: 360,
      width: container.clientWidth,
      layout: { background: { color: 'transparent' }, textColor: text },
      grid: { vertLines: { visible: false }, horzLines: { visible: false } },
    });
    const series = chart.addSeries(CandlestickSeries, {
      upColor: up,
      downColor: down,
      wickUpColor: up,
      wickDownColor: down,
      borderVisible: false,
    });
    series.setData(toSeriesData(candlesRef.current));
    chart.timeScale().fitContent();

    const observer = new ResizeObserver(() => {
      chart.applyOptions({ width: container.clientWidth });
    });
    observer.observe(container);

    chartRef.current = chart;
    seriesRef.current = series;

    return () => {
      observer.disconnect();
      chart.remove();
      chartRef.current = null;
      seriesRef.current = null;
    };
  }, []);

  useEffect(() => {
    seriesRef.current?.setData(toSeriesData(candles));
    chartRef.current?.timeScale().fitContent();
  }, [candles]);

  return <div ref={containerRef} className={styles.chart} data-testid="candle-chart" />;
}
```

`CandleChart.module.css`:

```css
.chart {
  width: 100%;
  min-height: 360px;
}
```

- [ ] **Step 6: Run to verify green**

```bash
pnpm --filter @marketpulse/dashboard test -- CandleChart
```

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(dashboard): CandleChart — lightweight-charts lifecycle behind the lazy boundary"
```

---

### Task 5: useCandles + PriceHistoryScreen

**Files:**
- Create: `apps/dashboard/src/features/history/useCandles.ts`
- Create: `apps/dashboard/src/features/history/PriceHistoryScreen.tsx`
- Create: `apps/dashboard/src/features/history/PriceHistoryScreen.module.css`
- Test: `apps/dashboard/src/features/history/PriceHistoryScreen.test.tsx`

**Interfaces:**
- Consumes: `apiClient.getCandles`, `CandleInterval` (Task 1), `CandleChart` (Task 4).
- Produces: `useCandles(ticker: string, interval: CandleInterval)`; `PriceHistoryScreen` as **default export** (React.lazy requires it) reading `:ticker` from route params. Task 6 mounts it at `/prices/:ticker`.

- [ ] **Step 1: Write the failing tests**

`PriceHistoryScreen.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';

const chartProps = vi.fn();
vi.mock('./CandleChart', () => ({
  CandleChart: (props: { candles: unknown[] }) => {
    chartProps(props);
    return <div data-testid="candle-chart-mock" />;
  },
}));

import PriceHistoryScreen from './PriceHistoryScreen';

const CANDLES_OK = {
  ticker: 'IVV',
  interval: '1m',
  candles: [{ t: '2026-08-06T10:00:00+00:00', o: 10, h: 12, l: 9, c: 11 }],
};

const requests: string[] = [];
const server = setupServer(
  http.get('http://localhost:5100/api/v1/prices/:ticker/candles', ({ request, params }) => {
    requests.push(request.url);
    if (params['ticker'] === 'ZZZZ') {
      return HttpResponse.json(
        { title: 'unknown-ticker', status: 404 },
        { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
      );
    }
    const interval = new URL(request.url).searchParams.get('interval') ?? '1m';
    return HttpResponse.json({ ...CANDLES_OK, interval });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => {
  server.resetHandlers();
  requests.length = 0;
  chartProps.mockClear();
});
afterAll(() => server.close());

function renderAt(path: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/prices/:ticker" element={<PriceHistoryScreen />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('PriceHistoryScreen', () => {
  it('renders the chart once candles arrive', async () => {
    renderAt('/prices/IVV');

    expect(screen.getByRole('status')).toBeInTheDocument(); // loading
    await screen.findByTestId('candle-chart-mock');
    expect(chartProps).toHaveBeenCalledWith(
      expect.objectContaining({ candles: CANDLES_OK.candles }),
    );
    expect(screen.getByRole('heading', { name: /IVV — price history/ })).toBeInTheDocument();
  });

  it('switching interval requests new candles and reflects aria-pressed', async () => {
    renderAt('/prices/IVV');
    await screen.findByTestId('candle-chart-mock');

    await userEvent.click(screen.getByRole('button', { name: '5m' }));

    await waitFor(() =>
      expect(requests.some((u) => new URL(u).searchParams.get('interval') === '5m')).toBe(true),
    );
    expect(screen.getByRole('button', { name: '5m' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: '1m' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('shows the unknown-ticker state with a way back', async () => {
    renderAt('/prices/ZZZZ');

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/unknown ticker/i);
    expect(screen.getByRole('link', { name: /back to watchlist/i })).toHaveAttribute('href', '/');
  });

  it('shows an explicit empty state for a known ticker with no data', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/prices/:ticker/candles', () =>
        HttpResponse.json({ ticker: 'IVV', interval: '1m', candles: [] }),
      ),
    );

    renderAt('/prices/IVV');

    await screen.findByText(/no history in this range yet/i);
    expect(screen.queryByTestId('candle-chart-mock')).not.toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify they fail**

```bash
pnpm --filter @marketpulse/dashboard test -- PriceHistoryScreen
```

- [ ] **Step 3: Implement the hook**

`useCandles.ts`:

```ts
import { ApiError, type CandleInterval } from '@marketpulse/api-client';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../../api';

/** Windows sized against 7a's 1000-bucket cap and its 7-day retention. */
const WINDOW_MS: Record<CandleInterval, number> = {
  '1m': 6 * 60 * 60 * 1000, // 360 buckets
  '5m': 24 * 60 * 60 * 1000, // 288
  '1h': 7 * 24 * 60 * 60 * 1000, // 168
  '1d': 7 * 24 * 60 * 60 * 1000, // ≤7 — thin until retention grows, and the UI says so
};

const FRESHNESS: Record<CandleInterval, { staleTime: number; refetchInterval: number | false }> = {
  '1m': { staleTime: 30_000, refetchInterval: 60_000 },
  '5m': { staleTime: 30_000, refetchInterval: 60_000 },
  '1h': { staleTime: 300_000, refetchInterval: false },
  '1d': { staleTime: 300_000, refetchInterval: false },
};

export function useCandles(ticker: string, interval: CandleInterval) {
  return useQuery({
    queryKey: ['candles', ticker, interval] as const,
    queryFn: ({ signal }) => {
      const to = new Date();
      const from = new Date(to.getTime() - WINDOW_MS[interval]);
      return apiClient.getCandles(ticker, interval, from.toISOString(), to.toISOString(), signal);
    },
    staleTime: FRESHNESS[interval].staleTime,
    refetchInterval: FRESHNESS[interval].refetchInterval,
    // An unknown ticker is not transient; everything else gets the default treatment.
    retry: (failureCount, error) =>
      !(error instanceof ApiError && error.status === 404) && failureCount < 3,
  });
}
```

- [ ] **Step 4: Implement the screen**

`PriceHistoryScreen.tsx`:

```tsx
import { ApiError, type CandleInterval } from '@marketpulse/api-client';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { CandleChart } from './CandleChart';
import styles from './PriceHistoryScreen.module.css';
import { useCandles } from './useCandles';

const INTERVALS: CandleInterval[] = ['1m', '5m', '1h', '1d'];

/** Default export: this module is the React.lazy boundary — see App.tsx. */
export default function PriceHistoryScreen() {
  const params = useParams<{ ticker: string }>();
  const ticker = (params.ticker ?? '').toUpperCase();
  const [interval, setInterval] = useState<CandleInterval>('1m');
  const { data, isPending, isError, error } = useCandles(ticker, interval);

  return (
    <section aria-labelledby="history-heading">
      <div className={styles.header}>
        <h2 className={styles.heading} id="history-heading">
          {ticker} — price history
        </h2>
        <Link to="/">Back to watchlist</Link>
      </div>

      <div className={styles.intervals} role="group" aria-label="Candle interval">
        {INTERVALS.map((candidate) => (
          <button
            key={candidate}
            type="button"
            aria-pressed={candidate === interval}
            onClick={() => setInterval(candidate)}
            className={styles.intervalButton}
          >
            {candidate}
          </button>
        ))}
      </div>

      {isPending && <p role="status">Loading history…</p>}

      {isError && (
        <p role="alert">
          {error instanceof ApiError && error.errorCode === 'unknown-ticker'
            ? `Unknown ticker “${ticker}”.`
            : 'Could not load price history.'}
        </p>
      )}

      {data && data.candles.length === 0 && (
        <p role="status">No history in this range yet — data accrues while the feed runs.</p>
      )}

      {data && data.candles.length > 0 && <CandleChart candles={data.candles} />}
    </section>
  );
}
```

`PriceHistoryScreen.module.css`:

```css
.header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--mp-space-4);
}

.heading {
  margin: 0 0 var(--mp-space-4);
}

.intervals {
  display: flex;
  gap: var(--mp-space-2);
  margin-bottom: var(--mp-space-4);
}

.intervalButton {
  background: var(--mp-surface-raised);
  color: var(--mp-text-primary);
  border: 1px solid var(--mp-border);
  border-radius: var(--mp-radius-sm);
  padding: var(--mp-space-1) var(--mp-space-3);
  font-family: var(--mp-font-mono);
  cursor: pointer;
}

.intervalButton[aria-pressed='true'] {
  border-color: var(--mp-accent);
  color: var(--mp-accent);
}
```

(Check the exact semantic token names against `packages/ui/src/tokens/tokens.css` — surface/border tokens must exist; substitute the file's actual names if they differ.)

- [ ] **Step 5: Run to verify green**

```bash
pnpm --filter @marketpulse/dashboard test -- PriceHistoryScreen
```

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(dashboard): price-history screen — interval switcher, SWR candles, honest states"
```

---

### Task 6: Lazy route + chunk-split verification

**Files:**
- Modify: `apps/dashboard/src/App.tsx`

**Interfaces:**
- Consumes: `PriceHistoryScreen` default export (Task 5).
- Produces: route `/prices/:ticker` behind `ProtectedRoute` + `Suspense`; `lightweight-charts` verified absent from the entry chunk.

- [ ] **Step 1: Wire the lazy route**

In `App.tsx`: replace the static import pattern for this one screen —

```tsx
import { Suspense, lazy } from 'react';

// The chart library rides this chunk and no other — the README's route-based
// code-splitting promise, verified against the build output in this slice.
const PriceHistoryScreen = lazy(() => import('./features/history/PriceHistoryScreen'));
```

and add inside `<Routes>` beside the `/portfolio` route:

```tsx
<Route
  path="/prices/:ticker"
  element={
    <ProtectedRoute>
      <Suspense fallback={<p role="status">Loading chart…</p>}>
        <PriceHistoryScreen />
      </Suspense>
    </ProtectedRoute>
  }
/>
```

- [ ] **Step 2: Verify the whole dashboard suite still passes**

```bash
pnpm --filter @marketpulse/dashboard test && pnpm -r typecheck
```

- [ ] **Step 3: Build and verify the chunk split**

```bash
pnpm --filter @marketpulse/dashboard build
grep -l -i 'lightweight' apps/dashboard/dist/assets/*.js
ls -la apps/dashboard/dist/assets/
```

Expected: exactly one chunk file matches (the lazy `PriceHistoryScreen-*.js` chunk), and it is NOT the `index-*.js` entry chunk. Record in your report: which files matched, and the entry-chunk size compared against `git stash`-free baseline knowledge (record current sizes; a before-number from `test` branch is welcome but optional). If the grep matches the entry chunk, the lazy boundary leaked — find the import that bypassed `features/history` and fix it before committing.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat(dashboard): lazy /prices/:ticker route — chart library isolated to its chunk"
```

---

### Task 7: Playwright chart journey

**Files:**
- Create: `tests/e2e/specs/chart.spec.ts`

**Interfaces:**
- Consumes: the running stack (Playwright's webServer config boots API + preview build), the register flow helpers used by `portfolio.spec.ts`.

- [ ] **Step 1: Read the existing spec pattern**

Open `tests/e2e/specs/portfolio.spec.ts` and reuse its fresh-user registration helper/pattern verbatim (import it if shared, copy the local pattern if not).

- [ ] **Step 2: Write the journey**

`tests/e2e/specs/chart.spec.ts` (adapt the register helper to the real pattern found in Step 1):

```ts
import { expect, test } from '@playwright/test';

// Follow portfolio.spec.ts's fresh-user pattern here — register via the UI with a unique
// email, which leaves the browser authenticated.

test('watchlist ticker links to a rendering candle chart', async ({ page }) => {
  await registerFreshUser(page);

  // Add a seeded ticker
  await page.getByLabel('Add ticker').fill('IVV');
  await page.getByRole('button', { name: 'Add' }).click();
  await expect(page.getByRole('link', { name: 'IVV price history' })).toBeVisible();

  // Sparkline: the API has been running since global-setup, so minute-buckets exist by
  // the time this (later-alphabetical) spec runs. Generous timeout: the sparkline query
  // fetches on mount.
  await expect(page.locator('polyline').first()).toBeVisible({ timeout: 30_000 });

  // Chart route — lazy chunk loads, canvas renders real candles
  await page.getByRole('link', { name: 'IVV price history' }).click();
  await expect(page.getByRole('heading', { name: /IVV — price history/ })).toBeVisible();
  await expect(page.locator('canvas').first()).toBeVisible({ timeout: 20_000 });

  // Interval switch keeps a live chart
  await page.getByRole('button', { name: '5m' }).click();
  await expect(page.getByRole('button', { name: '5m' })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('canvas').first()).toBeVisible();
});
```

If the sparkline assertion proves flaky against a freshly-started API (fewer than two
minute-buckets persisted yet), relax it to asserting the Trend column exists and record the
deviation + reason in your report — the sparkline's rendering is already unit-tested; the
journey's job is the real chunk + canvas.

- [ ] **Step 3: Run the journey (requires Docker for the API's SQL Server via docker compose, per the existing e2e setup — read tests/e2e/global-setup.ts and package.json scripts first and use the documented invocation)**

```bash
cd tests/e2e && pnpm exec playwright test specs/chart.spec.ts
```

Expected: PASS. Then run the full e2e suite once:

```bash
pnpm exec playwright test
```

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "test(e2e): chart journey — sparkline, lazy route, real canvas, interval switch"
```

---

### Task 8: Close the slice — roadmap, full gate, manual check

**Files:**
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Update the roadmap in the closing commit (its own rule)**

Completed-slices row for 7b (dense factual voice: sparklines, lazy chart route, chunk-split verified, SWR policy, journey); Phase 2 status → **Done** (slice 7 complete); remove the 7b remaining-slices entry.

- [ ] **Step 2: Full gate**

```bash
pnpm -r typecheck && pnpm -r test
pnpm --filter @marketpulse/dashboard build
dotnet build -c Release && dotnet test   # backend untouched — prove it
```

- [ ] **Step 3: Manual check against the running stack**

Dev API + Vite dev server; verify: sparklines visible on the watchlist (within two refetch windows of first data), chart renders for IVV on all four intervals (1d will be thin — expected), `/prices/ZZZZ` shows the unknown-ticker state with a working back link. Record outcomes + one screenshot-worthy observation in the report.

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "docs: close slice 7b — phase 2 done, chart UI shipped"
```

Do **not** merge to `test` — that is the finishing-a-development-branch flow, after the final whole-branch review.

---

## Self-review notes (already applied)

- **Spec coverage:** contracts (T1), sparkline component (T2), watchlist integration + link (T3), chart component + dependency (T4), screen + hooks + states + SWR policy (T5), lazy route + chunk verification (T6), journey (T7), roadmap close + gate + manual check (T8). Spec's error-surface table maps to T5's tests; "deliberately not tested" items are respected (no chart-rendering assertions beyond canvas visibility; no CI bundle budget).
- **Known judgment point, pre-decided:** the e2e sparkline assertion may be relaxed to column-presence if minute-bucket data is too fresh — deviation recorded in the task report, unit tests carry the sparkline contract.
- **Type consistency:** `Candle{t,o,h,l,c}` (T1) feeds `CandleChart` conversion (T4) and MSW fixtures (T5); `CandleInterval` is the single interval type everywhere; `PriceHistoryScreen` default export (T5) matches the `React.lazy` import (T6); `Sparkline` prop name `points` used in T2 and T3.
- **House-pattern checks embedded:** timestamps as plain `z.string()` (matches existing schemas), semantic-token-only CSS (noPrimitiveLeak), `renderScreen` router-wrap verification (T3), token-name verification against tokens.css (T5), v4/v5 lightweight-charts API contingency (T4).
