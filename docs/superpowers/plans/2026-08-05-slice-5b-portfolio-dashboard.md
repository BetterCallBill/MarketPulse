# Slice 5b — Portfolio Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `/portfolio` route showing holdings with live unrealised P&L (derived at render from the price stream), a trade form with submission-scoped idempotency keys, transaction history with load-more, header navigation, and the Playwright journey — frontend only, against 5a's merged API.

**Architecture:** `features/portfolio/` mirrors the existing feature folders: TanStack Query hooks over new api-client methods, components composed from `packages/ui` primitives, live data joined to server truth at render time per ADR-007. No backend changes of any kind.

**Tech Stack:** React 18 + react-router `NavLink`, TanStack Query, zod (`packages/api-client`), CSS modules on `--mp-*` tokens, Vitest + msw + testing-library, Playwright.

## Global Constraints

- Branch: `feature/slice-5b-portfolio-dashboard` (exists; spec committed). Merges into `test`.
- Conventional commits, **no Co-Authored-By trailer**.
- Spec: `docs/superpowers/specs/2026-08-05-portfolio-dashboard-design.md`. Out of scope there is out of scope here: no backend changes, no charts, no price prefill, no optimistic trade updates, no sell-from-row shortcuts, no tickers autocomplete.
- No `fetch` in the dashboard; CSS modules use only `--mp-*` tokens.
- TDD: failing tests first with RED/GREEN evidence wherever a failing test is meaningful.
- Run frontend tests with `pnpm --filter <pkg> test -- --run`; e2e with `pnpm --dir tests/e2e exec playwright test` (compose infra up; the Playwright harness already boots API + dashboard + Alerts worker).
- 5a wire shapes (camelCase): `PortfolioDto { holdings: HoldingDto[], totalRealisedPnL }`, `HoldingDto { ticker, units, averageCost, realisedPnL }`, `TransactionDto { id, ticker, side ("Buy"|"Sell"), units, price, occurredUtc, recordedUtc }`. Errors: 400 `unknown-ticker`/`invalid-side`/`invalid-units`/`invalid-price`/`invalid-paging`, 422 `insufficient-holdings`/`idempotency-key-reuse`, 409 `concurrent-update`/`idempotency-in-flight`.
- Contract copy (the e2e journey asserts on these): nav links named exactly `Watchlist` and `Portfolio`; portfolio page heading exactly `Portfolio`; trade form accessible names `Ticker`, `Side`, `Units`, `Price`, submit button `Record trade`; holdings table section heading `Holdings`; history section heading `History` with button `Load more`.

---

### Task 1: api-client — portfolio schemas and methods

**Files:**
- Modify: `packages/api-client/src/schemas.ts`
- Modify: `packages/api-client/src/client.ts`
- Test: `packages/api-client/src/schemas.test.ts`, `packages/api-client/src/client.test.ts`

**Interfaces:**
- Produces (later tasks bind to these exact names):
  - Schemas/types: `holdingSchema`, `Holding`, `portfolioSchema`, `Portfolio`, `transactionSchema`, `Transaction`, `tradeSideSchema` (`'Buy' | 'Sell'`), `TradeSide`.
  - Methods: `getPortfolio(signal?): Promise<Portfolio>`, `getTransactions(skip: number, take: number, signal?): Promise<Transaction[]>`, `recordTransaction(trade: { ticker: string; side: TradeSide; units: number; price: number }, idempotencyKey: string, signal?): Promise<Portfolio>`.

- [ ] **Step 1: Write the failing schema tests**

Append to `schemas.test.ts`:

```ts
describe('portfolio schemas', () => {
  it('parses a portfolio as the API serialises it', () => {
    const p = portfolioSchema.parse({
      holdings: [
        { ticker: 'IVV', units: 10.5, averageCost: 60.25, realisedPnL: 100 },
      ],
      totalRealisedPnL: 100,
    });

    expect(p.holdings[0]?.ticker).toBe('IVV');
    expect(p.totalRealisedPnL).toBe(100);
  });

  it('parses a transaction and rejects a bad side', () => {
    const t = transactionSchema.parse({
      id: 't1',
      ticker: 'IVV',
      side: 'Buy',
      units: 10,
      price: 60,
      occurredUtc: '2026-08-05T00:00:00+00:00',
      recordedUtc: '2026-08-05T00:00:00+00:00',
    });

    expect(t.side).toBe('Buy');

    expect(() => transactionSchema.parse({ ...t, side: 'Hold' })).toThrow();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `pnpm --filter @marketpulse/api-client test -- --run`
Expected: FAIL — schemas don't exist.

- [ ] **Step 3: Add the schemas**

Append to `schemas.ts`:

```ts
export const tradeSideSchema = z.enum(['Buy', 'Sell']);

export const holdingSchema = z.object({
  ticker: z.string().min(1).max(8),
  units: z.number(),
  averageCost: z.number(),
  realisedPnL: z.number(),
});

export const portfolioSchema = z.object({
  holdings: z.array(holdingSchema),
  totalRealisedPnL: z.number(),
});

export const transactionSchema = z.object({
  id: z.string(),
  ticker: z.string().min(1).max(8),
  side: tradeSideSchema,
  units: z.number().positive(),
  price: z.number().positive(),
  occurredUtc: z.string(),
  recordedUtc: z.string(),
});

export type TradeSide = z.infer<typeof tradeSideSchema>;
export type Holding = z.infer<typeof holdingSchema>;
export type Portfolio = z.infer<typeof portfolioSchema>;
export type Transaction = z.infer<typeof transactionSchema>;
```

(`units` on `holdingSchema` is deliberately not `.positive()` — a holding sold to zero is a real row the API returns.)

- [ ] **Step 4: Write the failing client tests**

Append to `client.test.ts` (existing `jsonResponse`/`fetchMock`/`BASE` helpers):

```ts
it('records a trade with the idempotency and CSRF headers attached', async () => {
  fetchMock.mockResolvedValue(
    jsonResponse({ holdings: [], totalRealisedPnL: 0 }, 201),
  );

  await createApiClient(BASE).recordTransaction(
    { ticker: 'IVV', side: 'Buy', units: 10, price: 60 },
    'key-123',
  );

  expect(fetchMock.mock.calls[0]?.[0]).toBe(`${BASE}/api/v1/portfolio/transactions`);
  const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
  expect(init.method).toBe('POST');
  const headers = init.headers as Record<string, string>;
  expect(headers['Idempotency-Key']).toBe('key-123');
  expect(headers['X-CSRF-Token']).toBe('nonce-123');
  expect(JSON.parse(init.body as string)).toEqual({
    ticker: 'IVV',
    side: 'Buy',
    units: 10,
    price: 60,
  });
});

it('pages transactions with skip and take', async () => {
  fetchMock.mockResolvedValue(jsonResponse([]));

  await createApiClient(BASE).getTransactions(20, 10);

  expect(fetchMock.mock.calls[0]?.[0]).toBe(
    `${BASE}/api/v1/portfolio/transactions?skip=20&take=10`,
  );
});

it('parses the portfolio from getPortfolio', async () => {
  fetchMock.mockResolvedValue(
    jsonResponse({
      holdings: [{ ticker: 'IVV', units: 10, averageCost: 60, realisedPnL: 0 }],
      totalRealisedPnL: 0,
    }),
  );

  const portfolio = await createApiClient(BASE).getPortfolio();

  expect(portfolio.holdings[0]?.averageCost).toBe(60);
});
```

- [ ] **Step 5: Add the client methods**

Append to the returned object in `client.ts` (extend the schema imports accordingly):

```ts
    getPortfolio: (signal?: AbortSignal): Promise<Portfolio> =>
      request('/api/v1/portfolio', { method: 'GET', signal }, (d) =>
        portfolioSchema.parse(d),
      ),

    getTransactions: (skip: number, take: number, signal?: AbortSignal): Promise<Transaction[]> =>
      request(
        `/api/v1/portfolio/transactions?skip=${skip}&take=${take}`,
        { method: 'GET', signal },
        (d) => z.array(transactionSchema).parse(d),
      ),

    recordTransaction: (
      trade: { ticker: string; side: TradeSide; units: number; price: number },
      idempotencyKey: string,
      signal?: AbortSignal,
    ): Promise<Portfolio> =>
      request(
        '/api/v1/portfolio/transactions',
        {
          method: 'POST',
          body: JSON.stringify(trade),
          headers: { 'Idempotency-Key': idempotencyKey },
          signal,
        },
        (d) => portfolioSchema.parse(d),
      ),
```

(`request` spreads `init.headers` after the CSRF header, so both survive — the first client test pins that.)

- [ ] **Step 6: Run tests and build**

Run: `pnpm --filter @marketpulse/api-client test -- --run && pnpm --filter @marketpulse/api-client build`
Expected: PASS / clean.

- [ ] **Step 7: Commit**

```bash
git add packages/api-client/src
git commit -m "feat(api-client): portfolio schemas and trade recording with idempotency keys"
```

---

### Task 2: Navigation and the /portfolio route scaffold

**Files:**
- Create: `apps/dashboard/src/features/nav/Nav.tsx`
- Create: `apps/dashboard/src/features/nav/Nav.module.css`
- Create: `apps/dashboard/src/features/nav/Nav.test.tsx`
- Create: `apps/dashboard/src/features/portfolio/PortfolioScreen.tsx` (scaffold — heading + section placeholders filled by Tasks 4–6)
- Create: `apps/dashboard/src/features/portfolio/PortfolioScreen.module.css`
- Modify: `apps/dashboard/src/App.tsx`

**Interfaces:**
- Produces: `Nav()` — renders nothing without a session; two `NavLink`s named exactly `Watchlist` (`/`) and `Portfolio` (`/portfolio`) with `aria-current="page"` on the active one (NavLink's default). `/portfolio` route under `ProtectedRoute`.

- [ ] **Step 1: Write the failing Nav test**

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { MemoryRouter } from 'react-router-dom';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { Nav } from './Nav';

const server = setupServer(
  http.get('http://localhost:5100/api/v1/auth/me', () =>
    HttpResponse.json({ id: 'u1', email: 'billy@example.test' }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderNav(path = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Nav />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('Nav', () => {
  it('links both screens and marks the active one', async () => {
    renderNav('/portfolio');

    const portfolio = await screen.findByRole('link', { name: 'Portfolio' });
    expect(portfolio).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('link', { name: 'Watchlist' })).not.toHaveAttribute(
      'aria-current',
    );
  });

  it('renders nothing without a session', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/auth/me', () =>
        HttpResponse.json({ title: 'unauthorized', status: 401 }, { status: 401 }),
      ),
    );

    const { container } = renderNav();

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/nav`
Expected: FAIL — module doesn't exist.

- [ ] **Step 3: Implement**

`Nav.tsx`:

```tsx
import { NavLink } from 'react-router-dom';
import { useSession } from '../auth/useSession';
import styles from './Nav.module.css';

/** The app's whole navigation story: two screens, one NavLink each. */
export function Nav() {
  const { data: session } = useSession();
  if (!session) return null;

  return (
    <nav aria-label="Primary">
      <NavLink to="/" end className={styles.link}>
        Watchlist
      </NavLink>
      <NavLink to="/portfolio" className={styles.link}>
        Portfolio
      </NavLink>
    </nav>
  );
}
```

(`end` on `/` so it isn't marked active on `/portfolio`. NavLink sets `aria-current="page"` on the active link by itself.)

`Nav.module.css`:

```css
.link {
  padding: var(--mp-space-1) var(--mp-space-2);
  border-radius: var(--mp-radius-sm);
  color: var(--mp-text-secondary);
  text-decoration: none;
  font-size: 0.875rem;
}

.link:hover {
  color: var(--mp-text-primary);
  background: var(--mp-surface-hover);
}

.link[aria-current='page'] {
  color: var(--mp-text-primary);
  font-weight: 600;
}

nav[aria-label='Primary'] {
  display: flex;
  gap: var(--mp-space-2);
}
```

(If the bare-element selector trips the css-module conventions, wrap the two links in a `<nav className={styles.nav}>` class instead — match whatever the token-leak/test tooling accepts.)

`PortfolioScreen.tsx` scaffold (Tasks 4–6 fill the sections):

```tsx
import styles from './PortfolioScreen.module.css';

export function PortfolioScreen() {
  return (
    <section aria-labelledby="portfolio-heading" className={styles.screen}>
      <h2 id="portfolio-heading" className={styles.heading}>
        Portfolio
      </h2>
      {/* Holdings, TradeForm, History land in Tasks 4–6 */}
    </section>
  );
}
```

`PortfolioScreen.module.css`:

```css
.screen {
  display: flex;
  flex-direction: column;
  gap: var(--mp-space-5);
}

.heading {
  margin: 0;
  font-size: 1.25rem;
}
```

`App.tsx` — add the Nav beside the wordmark and the route:

```tsx
import { Nav } from './features/nav/Nav';
import { PortfolioScreen } from './features/portfolio/PortfolioScreen';
```

```tsx
          <header className={styles.header}>
            <h1 className={styles.wordmark}>MarketPulse Pro</h1>
            <Nav />
            <div className={styles.controls}>
              <NotificationBell />
              <SignOutButton />
            </div>
          </header>
```

```tsx
              <Route
                path="/portfolio"
                element={
                  <ProtectedRoute>
                    <PortfolioScreen />
                  </ProtectedRoute>
                }
              />
```

- [ ] **Step 4: Run the dashboard suite**

Run: `pnpm --filter @marketpulse/dashboard test -- --run`
Expected: PASS (Nav tests plus all pre-existing; nothing else fetches on `/` so no msw fallout expected — if any existing test renders `App` and now trips on the nav's `/auth/me`, it already had that handler for the bell).

- [ ] **Step 5: Commit**

```bash
git add apps/dashboard/src
git commit -m "feat(dashboard): header navigation and the /portfolio route"
```

---

### Task 3: Portfolio hooks

**Files:**
- Create: `apps/dashboard/src/features/portfolio/usePortfolio.ts`
- Create: `apps/dashboard/src/features/portfolio/usePortfolio.test.tsx`

**Interfaces:**
- Consumes: `apiClient`; `Portfolio`, `Transaction`, `TradeSide`, `ApiError` from `@marketpulse/api-client`.
- Produces (Tasks 4–6 bind to these):
  - `portfolioKey = ['portfolio'] as const`, `transactionsKey = ['transactions'] as const`.
  - `usePortfolio()` — query over `getPortfolio`.
  - `useTransactions(pageSize = 20)` — returns `{ transactions, loadMore, hasMore, isPending }`; accumulates pages; a `['transactions']` invalidation resets to the first page.
  - `useRecordTransaction()` — mutation `{ trade: { ticker, side, units, price }, idempotencyKey }` → invalidates both keys on success.

- [ ] **Step 1: Write the failing hook tests**

`usePortfolio.test.tsx` (msw + renderHook, the established pattern):

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import type { ReactNode } from 'react';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import {
  usePortfolio,
  useRecordTransaction,
  useTransactions,
} from './usePortfolio';

const page0 = [
  { id: 't3', ticker: 'IVV', side: 'Sell', units: 4, price: 70, occurredUtc: '2026-08-05T03:00:00+00:00', recordedUtc: '2026-08-05T03:00:00+00:00' },
  { id: 't2', ticker: 'IVV', side: 'Buy', units: 5, price: 65, occurredUtc: '2026-08-05T02:00:00+00:00', recordedUtc: '2026-08-05T02:00:00+00:00' },
];
const page1 = [
  { id: 't1', ticker: 'IVV', side: 'Buy', units: 10, price: 60, occurredUtc: '2026-08-05T01:00:00+00:00', recordedUtc: '2026-08-05T01:00:00+00:00' },
];

const server = setupServer(
  http.get('http://localhost:5100/api/v1/portfolio', () =>
    HttpResponse.json({
      holdings: [{ ticker: 'IVV', units: 11, averageCost: 62.27, realisedPnL: 31 }],
      totalRealisedPnL: 31,
    }),
  ),
  // Slices by take: the hook fetches a growing newest-first window (skip stays 0).
  http.get('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
    const url = new URL(request.url);
    const take = Number(url.searchParams.get('take'));
    return HttpResponse.json([...page0, ...page1].slice(0, take));
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function wrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return {
    client,
    Wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    ),
  };
}

describe('usePortfolio', () => {
  it('loads the portfolio', async () => {
    const { Wrapper } = wrapper();
    const { result } = renderHook(() => usePortfolio(), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.data?.totalRealisedPnL).toBe(31));
  });
});

describe('useTransactions', () => {
  it('accumulates pages via loadMore and reports hasMore from a full page', async () => {
    const { Wrapper } = wrapper();
    const { result } = renderHook(() => useTransactions(2), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.transactions).toHaveLength(2));
    expect(result.current.hasMore).toBe(true); // page 0 came back full

    result.current.loadMore();

    await waitFor(() => expect(result.current.transactions).toHaveLength(3));
    expect(result.current.transactions[2]?.id).toBe('t1');
    expect(result.current.hasMore).toBe(false); // short page ends it
  });
});

describe('useRecordTransaction', () => {
  it('sends the key and invalidates both queries on success', async () => {
    let seenKey: string | null = null;
    server.use(
      http.post('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
        seenKey = request.headers.get('Idempotency-Key');
        return HttpResponse.json({ holdings: [], totalRealisedPnL: 0 }, { status: 201 });
      }),
    );

    const { client, Wrapper } = wrapper();
    const invalidated: unknown[] = [];
    const original = client.invalidateQueries.bind(client);
    client.invalidateQueries = ((filters?: { queryKey?: unknown }) => {
      invalidated.push(filters?.queryKey);
      return original(filters as never);
    }) as typeof client.invalidateQueries;

    const { result } = renderHook(() => useRecordTransaction(), { wrapper: Wrapper });

    result.current.mutate({
      trade: { ticker: 'IVV', side: 'Buy', units: 1, price: 60 },
      idempotencyKey: 'key-abc',
    });

    await waitFor(() => expect(seenKey).toBe('key-abc'));
    await waitFor(() =>
      expect(invalidated).toEqual(
        expect.arrayContaining([['portfolio'], ['transactions']]),
      ),
    );
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/portfolio`
Expected: FAIL.

- [ ] **Step 3: Implement `usePortfolio.ts`**

```tsx
import {
  ApiError,
  type Portfolio,
  type TradeSide,
  type Transaction,
} from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { apiClient } from '../../api';

export const portfolioKey = ['portfolio'] as const;
export const transactionsKey = ['transactions'] as const;

export function usePortfolio() {
  return useQuery({
    queryKey: portfolioKey,
    queryFn: ({ signal }) => apiClient.getPortfolio(signal),
  });
}

/**
 * Load-more over the server's skip/take paging. Pages accumulate in the query cache under
 * ['transactions', pageCount]; invalidating ['transactions'] (as a recorded trade does)
 * refetches every held page — and because the new trade is by definition at the head, the
 * simple reset-to-consistency beats reconciling a fresh head against stale deeper pages.
 */
export function useTransactions(pageSize = 20) {
  const [pages, setPages] = useState(1);

  const { data, isPending } = useQuery({
    queryKey: [...transactionsKey, pages, pageSize],
    queryFn: ({ signal }) => apiClient.getTransactions(0, pages * pageSize, signal),
  });

  const transactions: Transaction[] = data ?? [];

  return {
    transactions,
    isPending,
    // A full window means the server may have more; a short one means we drained it.
    hasMore: transactions.length === pages * pageSize,
    loadMore: () => setPages((p) => p + 1),
  };
}

export function useRecordTransaction() {
  const queryClient = useQueryClient();

  return useMutation<
    Portfolio,
    ApiError,
    {
      trade: { ticker: string; side: TradeSide; units: number; price: number };
      idempotencyKey: string;
    }
  >({
    mutationFn: ({ trade, idempotencyKey }) =>
      apiClient.recordTransaction(trade, idempotencyKey),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: portfolioKey });
      void queryClient.invalidateQueries({ queryKey: transactionsKey });
    },
  });
}
```

Implementation note (visible in the code above, deliberate): `useTransactions` grows the
window (`take = pages × pageSize`, `skip = 0`) rather than stitching separate skip-pages in
client state. One request re-fetches a consistent newest-first window, the invalidation
story is trivial (the key includes `pages`, and invalidating the `['transactions']` prefix
hits every variant), and the server caps `take` at 100 — five load-mores at the default page
size, which is more history than the product has readers for. If the reviewer of this task
judges true skip-paging necessary, that is a spec conversation, not a silent rewrite. The
`hasMore` heuristic (full window ⇒ assume more) can show one dead "Load more" click at an
exact boundary; acceptable and noted here.

The Step 1 test drives `useTransactions(2)` and expects `transactions[2]` after one
`loadMore`; the growing window satisfies it (`take=4`, the take-slicing handler returns 3).

- [ ] **Step 4: Run to verify pass**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/portfolio`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/dashboard/src/features/portfolio
git commit -m "feat(dashboard): portfolio query, growing-window history, and trade mutation hooks"
```

---

### Task 4: HoldingsTable with live unrealised P&L

**Files:**
- Create: `apps/dashboard/src/features/portfolio/HoldingsTable.tsx`
- Create: `apps/dashboard/src/features/portfolio/HoldingsTable.module.css`
- Create: `apps/dashboard/src/features/portfolio/HoldingsTable.test.tsx`
- Modify: `apps/dashboard/src/features/portfolio/PortfolioScreen.tsx` (mount the section)

**Interfaces:**
- Consumes: `usePortfolio`, `usePriceStream` (existing: `{ status, prices: { [ticker]: { price, direction, seq } } }`), `PriceCell`, `isStale`/`useNow` (the watchlist's usage pattern), `Panel` from `@marketpulse/ui`.
- Produces: `HoldingsTable()` — self-contained section with heading exactly `Holdings`; per-row unrealised P&L cell with `aria-label` `` `${ticker} unrealised P&L` ``; footer totals row.

- [ ] **Step 1: Write the failing tests**

`HoldingsTable.test.tsx` — mock `usePriceStream` (the AlertCell/watchlist test precedent), msw for the portfolio:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { HoldingsTable } from './HoldingsTable';

const prices: Record<string, { price: number; direction: 'up' | 'down' | 'neutral'; seq: number }> = {};

vi.mock('../prices/usePriceStream', () => ({
  usePriceStream: () => ({ status: 'connected' as const, prices }),
}));

const server = setupServer(
  http.get('http://localhost:5100/api/v1/portfolio', () =>
    HttpResponse.json({
      holdings: [
        { ticker: 'IVV', units: 10, averageCost: 60, realisedPnL: 25 },
        { ticker: 'NDQ', units: 4, averageCost: 50, realisedPnL: -5 },
      ],
      totalRealisedPnL: 20,
    }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => {
  server.resetHandlers();
  for (const key of Object.keys(prices)) delete prices[key];
});
afterAll(() => server.close());

function renderTable() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <HoldingsTable />
    </QueryClientProvider>,
  );
}

describe('HoldingsTable', () => {
  it('derives unrealised P&L from the live price at render', async () => {
    prices['IVV'] = { price: 65, direction: 'up', seq: 1 };
    prices['NDQ'] = { price: 45, direction: 'down', seq: 1 };

    renderTable();

    // 10 × (65 − 60) = +50; 4 × (45 − 50) = −20.
    expect(await screen.findByLabelText('IVV unrealised P&L')).toHaveTextContent('+$50.00');
    expect(screen.getByLabelText('NDQ unrealised P&L')).toHaveTextContent('−$20.00');
  });

  it('shows an em-dash for a holding whose ticker has not ticked, and for the total', async () => {
    prices['IVV'] = { price: 65, direction: 'up', seq: 1 };
    // NDQ never ticks.

    renderTable();

    expect(await screen.findByLabelText('NDQ unrealised P&L')).toHaveTextContent('—');
    // A partial sum lies: the footer total is also an em-dash.
    expect(screen.getByLabelText('Total unrealised P&L')).toHaveTextContent('—');
  });

  it('shows the empty state before any trade', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/portfolio', () =>
        HttpResponse.json({ holdings: [], totalRealisedPnL: 0 }),
      ),
    );

    renderTable();

    expect(
      await screen.findByText('No holdings yet. Record your first trade below.'),
    ).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/portfolio`
Expected: FAIL.

- [ ] **Step 3: Implement**

`HoldingsTable.tsx`:

```tsx
import { Panel } from '@marketpulse/ui';
import { PriceCell } from '../prices/PriceCell';
import { isStale } from '../prices/streamReducer';
import { useNow } from '../prices/useNow';
import { usePriceStream } from '../prices/usePriceStream';
import styles from './HoldingsTable.module.css';
import { usePortfolio } from './usePortfolio';

/** Signed money: the sign is the signal, colour only reinforces it. */
function signedMoney(value: number): string {
  const sign = value < 0 ? '−' : '+';
  return `${sign}$${Math.abs(value).toFixed(2)}`;
}

export function HoldingsTable() {
  const { data, isPending, isError } = usePortfolio();
  const stream = usePriceStream();
  const now = useNow();

  if (isPending) return <p>Loading portfolio…</p>;
  if (isError) return <p role="alert">Could not load your portfolio.</p>;

  const holdings = data.holdings;

  // ADR-007's third worked example: server truth × live stream, joined at render.
  const rows = holdings.map((h) => {
    const live = stream.prices[h.ticker]?.price;
    return {
      ...h,
      live,
      unrealised: live === undefined ? undefined : h.units * (live - h.averageCost),
    };
  });

  // A partial sum lies — the total is only a number when every held ticker has ticked.
  const totalUnrealised = rows.every((r) => r.unrealised !== undefined)
    ? rows.reduce((sum, r) => sum + (r.unrealised ?? 0), 0)
    : undefined;

  return (
    <section aria-labelledby="holdings-heading">
      <h3 id="holdings-heading" className={styles.heading}>
        Holdings
      </h3>
      <Panel className={styles.tableWrap}>
        {holdings.length === 0 ? (
          <p className={styles.empty}>No holdings yet. Record your first trade below.</p>
        ) : (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">Ticker</th>
                <th scope="col" className={styles.numeric}>Units</th>
                <th scope="col" className={styles.numeric}>Avg cost</th>
                <th scope="col" className={styles.numeric}>Last</th>
                <th scope="col" className={styles.numeric}>Realised P&L</th>
                <th scope="col" className={styles.numeric}>Unrealised P&L</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.ticker}>
                  <td className={styles.ticker}>{row.ticker}</td>
                  <td className={styles.numeric}>{row.units}</td>
                  <td className={styles.numeric}>${row.averageCost.toFixed(2)}</td>
                  <td className={styles.numeric}>
                    <PriceCell
                      ticker={row.ticker}
                      price={row.live}
                      stale={isStale(stream, row.ticker, now)}
                      disconnected={stream.status === 'reconnecting'}
                      direction={stream.prices[row.ticker]?.direction ?? 'neutral'}
                      seq={stream.prices[row.ticker]?.seq ?? 0}
                    />
                  </td>
                  <td
                    className={styles.numeric}
                    data-tone={row.realisedPnL < 0 ? 'down' : 'up'}
                  >
                    {signedMoney(row.realisedPnL)}
                  </td>
                  <td
                    className={styles.numeric}
                    aria-label={`${row.ticker} unrealised P&L`}
                    data-tone={
                      row.unrealised === undefined ? undefined : row.unrealised < 0 ? 'down' : 'up'
                    }
                  >
                    {row.unrealised === undefined ? '—' : signedMoney(row.unrealised)}
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th scope="row" colSpan={4}>Total</th>
                <td className={styles.numeric} data-tone={data.totalRealisedPnL < 0 ? 'down' : 'up'}>
                  {signedMoney(data.totalRealisedPnL)}
                </td>
                <td
                  className={styles.numeric}
                  aria-label="Total unrealised P&L"
                  data-tone={
                    totalUnrealised === undefined ? undefined : totalUnrealised < 0 ? 'down' : 'up'
                  }
                >
                  {totalUnrealised === undefined ? '—' : signedMoney(totalUnrealised)}
                </td>
              </tr>
            </tfoot>
          </table>
        )}
      </Panel>
    </section>
  );
}
```

`HoldingsTable.module.css` (mirror `WatchlistScreen.module.css`'s table vocabulary):

```css
.heading {
  margin: 0 0 var(--mp-space-2);
  font-size: 1rem;
}

.tableWrap {
  overflow-x: auto;
}

.table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9375rem;
}

.table th,
.table td {
  padding: var(--mp-space-2) var(--mp-space-3);
  text-align: left;
  border-bottom: 1px solid var(--mp-border-subtle);
}

.table tfoot th,
.table tfoot td {
  border-bottom: none;
  font-weight: 600;
}

.numeric {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

.ticker {
  font-weight: 600;
}

.table [data-tone='up'] {
  color: var(--mp-price-up);
}

.table [data-tone='down'] {
  color: var(--mp-price-down);
}

.empty {
  margin: 0;
  color: var(--mp-text-muted);
  padding: var(--mp-space-3);
}
```

Mount in `PortfolioScreen.tsx` (replace the placeholder comment):

```tsx
      <HoldingsTable />
```

- [ ] **Step 4: Run to verify pass**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/portfolio`
Expected: PASS. (The `−` in `signedMoney` is U+2212 minus, matching the test's expected strings — keep them identical.)

- [ ] **Step 5: Commit**

```bash
git add apps/dashboard/src/features/portfolio
git commit -m "feat(dashboard): holdings table with render-derived live unrealised P&L"
```

---

### Task 5: TradeForm with submission-scoped idempotency keys

**Files:**
- Create: `apps/dashboard/src/features/portfolio/TradeForm.tsx`
- Create: `apps/dashboard/src/features/portfolio/TradeForm.module.css`
- Create: `apps/dashboard/src/features/portfolio/TradeForm.test.tsx`
- Modify: `apps/dashboard/src/features/portfolio/PortfolioScreen.tsx` (mount below holdings)

**Interfaces:**
- Consumes: `useRecordTransaction`; `Alert`, `Button` from `@marketpulse/ui`.
- Produces: `TradeForm()` — accessible names exactly `Ticker`, `Side`, `Units`, `Price`; submit button `Record trade`; on 409 an inline message with a button `Retry` that resubmits with the same key.

- [ ] **Step 1: Write the failing tests**

`TradeForm.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { TradeForm } from './TradeForm';

const seenKeys: string[] = [];

const server = setupServer(
  http.post('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
    seenKeys.push(request.headers.get('Idempotency-Key') ?? '(none)');
    return HttpResponse.json({ holdings: [], totalRealisedPnL: 0 }, { status: 201 });
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => {
  server.resetHandlers();
  seenKeys.length = 0;
});
afterAll(() => server.close());

function renderForm() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <TradeForm />
    </QueryClientProvider>,
  );
}

async function fillAndSubmit() {
  await userEvent.type(screen.getByLabelText('Ticker'), 'IVV');
  await userEvent.selectOptions(screen.getByLabelText('Side'), 'Buy');
  await userEvent.type(screen.getByLabelText('Units'), '10');
  await userEvent.type(screen.getByLabelText('Price'), '60');
  await userEvent.click(screen.getByRole('button', { name: 'Record trade' }));
}

describe('TradeForm', () => {
  it('posts the trade with an idempotency key', async () => {
    renderForm();

    await fillAndSubmit();

    await waitFor(() => expect(seenKeys).toHaveLength(1));
    expect(seenKeys[0]).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('reuses the key on retry after a 409, and mints a new one for the next submission', async () => {
    let calls = 0;
    server.use(
      http.post('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
        seenKeys.push(request.headers.get('Idempotency-Key') ?? '(none)');
        calls += 1;
        if (calls === 1) {
          return HttpResponse.json(
            { title: 'idempotency-in-flight', status: 409, detail: 'Retry shortly.' },
            { status: 409 },
          );
        }
        return HttpResponse.json({ holdings: [], totalRealisedPnL: 0 }, { status: 201 });
      }),
    );

    renderForm();

    await fillAndSubmit();
    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    // Same submission → same key.
    await waitFor(() => expect(seenKeys).toHaveLength(2));
    expect(seenKeys[1]).toBe(seenKeys[0]);

    // Next deliberate submission → fresh key.
    await fillAndSubmit();
    await waitFor(() => expect(seenKeys).toHaveLength(3));
    expect(seenKeys[2]).not.toBe(seenKeys[0]);
  });

  it('surfaces the server detail on a rejected trade', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/portfolio/transactions', () =>
        HttpResponse.json(
          { title: 'insufficient-holdings', status: 422, detail: 'Cannot sell 6 units: only 5 held.' },
          { status: 422 },
        ),
      ),
    );

    renderForm();

    await fillAndSubmit();

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(
        'Cannot sell 6 units: only 5 held.',
      ),
    );
    // A definitive rejection offers no Retry — retrying the same key would just repeat it.
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
  });

  it('does not post with an empty required field and flags the input', async () => {
    renderForm();

    await userEvent.click(screen.getByRole('button', { name: 'Record trade' }));

    expect(screen.getByLabelText('Ticker')).toBeInvalid();
    expect(seenKeys).toHaveLength(0);
  });
});
```

- [ ] **Step 2: Run to verify failure**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/portfolio`
Expected: FAIL.

- [ ] **Step 3: Implement**

`TradeForm.tsx`:

```tsx
import { Alert, Button } from '@marketpulse/ui';
import { useRef, useState, type FormEvent } from 'react';
import styles from './TradeForm.module.css';
import { useRecordTransaction } from './usePortfolio';

/**
 * The idempotency key is scoped to a submission, not a request and not the form's
 * lifetime: minted when the user submits, reused verbatim by Retry (a resend of the
 * same intent), discarded the moment a new submission starts. This is the client half
 * of the contract the 5a filter implements.
 */
export function TradeForm() {
  const record = useRecordTransaction();
  const [ticker, setTicker] = useState('');
  const [side, setSide] = useState<'Buy' | 'Sell'>('Buy');
  const [units, setUnits] = useState('');
  const [price, setPrice] = useState('');
  const keyRef = useRef<string | null>(null);

  function submit(key: string) {
    record.mutate(
      {
        trade: { ticker: ticker.trim(), side, units: Number(units), price: Number(price) },
        idempotencyKey: key,
      },
      {
        onSuccess: () => {
          keyRef.current = null;
          setTicker('');
          setUnits('');
          setPrice('');
        },
      },
    );
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (ticker.trim() === '' || Number(units) <= 0 || Number(price) <= 0) return;

    // A new submission is new intent: fresh key, whatever happened before.
    keyRef.current = crypto.randomUUID();
    submit(keyRef.current);
  }

  function handleRetry() {
    // Same intent, same key — the server replays or reports in-flight, never duplicates.
    if (keyRef.current) submit(keyRef.current);
  }

  const retryable = record.isError && record.error.status === 409;

  return (
    <section aria-labelledby="trade-heading">
      <h3 id="trade-heading" className={styles.heading}>
        Record a trade
      </h3>
      <form className={styles.form} onSubmit={handleSubmit}>
        <input
          className={styles.field}
          aria-label="Ticker"
          value={ticker}
          onChange={(e) => setTicker(e.target.value)}
          maxLength={8}
          required
        />
        <select
          className={styles.field}
          aria-label="Side"
          value={side}
          onChange={(e) => setSide(e.target.value as 'Buy' | 'Sell')}
        >
          <option value="Buy">Buy</option>
          <option value="Sell">Sell</option>
        </select>
        <input
          className={styles.field}
          aria-label="Units"
          type="number"
          step="0.000001"
          min="0.000001"
          value={units}
          onChange={(e) => setUnits(e.target.value)}
          required
        />
        <input
          className={styles.field}
          aria-label="Price"
          type="number"
          step="0.01"
          min="0.01"
          value={price}
          onChange={(e) => setPrice(e.target.value)}
          required
        />
        <Button type="submit" disabled={record.isPending}>
          Record trade
        </Button>
      </form>

      {record.isError && (
        <div className={styles.error}>
          <Alert>
            {record.error.message}
            {record.error.correlationId && ` (ref: ${record.error.correlationId})`}
          </Alert>
          {retryable && (
            <Button variant="ghost" onClick={handleRetry} disabled={record.isPending}>
              Retry
            </Button>
          )}
        </div>
      )}
    </section>
  );
}
```

`TradeForm.module.css`:

```css
.heading {
  margin: 0 0 var(--mp-space-2);
  font-size: 1rem;
}

.form {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--mp-space-2);
}

.field {
  padding: var(--mp-space-1) var(--mp-space-2);
  border: 1px solid var(--mp-border-subtle);
  border-radius: var(--mp-radius-sm);
  background: var(--mp-surface-base);
  color: var(--mp-text-primary);
  font: inherit;
  width: 7rem;
}

.error {
  display: flex;
  align-items: center;
  gap: var(--mp-space-2);
  margin-top: var(--mp-space-2);
}
```

Check `ApiError` exposes `status` (it does — first constructor parameter). Mount `<TradeForm />` in `PortfolioScreen` below `<HoldingsTable />`.

- [ ] **Step 4: Run to verify pass**

Run: `pnpm --filter @marketpulse/dashboard test -- --run src/features/portfolio`
Expected: PASS. (jsdom supports `crypto.randomUUID`; if the test environment lacks it, polyfill in `src/test/setup.ts` rather than in product code.)

- [ ] **Step 5: Commit**

```bash
git add apps/dashboard/src/features/portfolio
git commit -m "feat(dashboard): trade form with submission-scoped idempotency keys"
```

---

### Task 6: Transaction history with load-more

**Files:**
- Create: `apps/dashboard/src/features/portfolio/TransactionHistory.tsx`
- Create: `apps/dashboard/src/features/portfolio/TransactionHistory.test.tsx`
- Modify: `apps/dashboard/src/features/portfolio/PortfolioScreen.tsx` (mount last)
- Modify: `apps/dashboard/src/features/portfolio/PortfolioScreen.module.css` (if a shared list style is needed)

**Interfaces:**
- Consumes: `useTransactions`; `Panel`, `Button` from `@marketpulse/ui`.
- Produces: section heading exactly `History`; button exactly `Load more` (hidden when `hasMore` is false); rows render `` `${side} ${units} ${ticker} @ $${price.toFixed(2)}` `` plus a localised time.

- [ ] **Step 1: Write the failing test**

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { TransactionHistory } from './TransactionHistory';

const fixture = [
  { id: 't3', ticker: 'IVV', side: 'Sell', units: 4, price: 70, occurredUtc: '2026-08-05T03:00:00+00:00', recordedUtc: '2026-08-05T03:00:00+00:00' },
  { id: 't2', ticker: 'IVV', side: 'Buy', units: 5, price: 65, occurredUtc: '2026-08-05T02:00:00+00:00', recordedUtc: '2026-08-05T02:00:00+00:00' },
  { id: 't1', ticker: 'IVV', side: 'Buy', units: 10, price: 60, occurredUtc: '2026-08-05T01:00:00+00:00', recordedUtc: '2026-08-05T01:00:00+00:00' },
];

const server = setupServer(
  http.get('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
    const take = Number(new URL(request.url).searchParams.get('take'));
    return HttpResponse.json(fixture.slice(0, take));
  }),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderHistory() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <TransactionHistory pageSize={2} />
    </QueryClientProvider>,
  );
}

describe('TransactionHistory', () => {
  it('lists newest first and loads more until the history is drained', async () => {
    renderHistory();

    expect(await screen.findByText('Sell 4 IVV @ $70.00')).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(2);

    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));

    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(3));
    // Drained: the short page removes the button.
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('shows the empty state for a fresh user', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/portfolio/transactions', () =>
        HttpResponse.json([]),
      ),
    );

    renderHistory();

    expect(await screen.findByText('No trades recorded yet.')).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify failure, then implement**

`TransactionHistory.tsx`:

```tsx
import { Button, Panel } from '@marketpulse/ui';
import styles from './PortfolioScreen.module.css';
import { useTransactions } from './usePortfolio';

export function TransactionHistory({ pageSize = 20 }: { pageSize?: number }) {
  const { transactions, hasMore, loadMore, isPending } = useTransactions(pageSize);

  return (
    <section aria-labelledby="history-heading">
      <h3 id="history-heading" className={styles.sectionHeading}>
        History
      </h3>
      <Panel>
        {transactions.length === 0 && !isPending ? (
          <p className={styles.empty}>No trades recorded yet.</p>
        ) : (
          <ul className={styles.historyList}>
            {transactions.map((t) => (
              <li key={t.id} className={styles.historyItem}>
                <span>{`${t.side} ${t.units} ${t.ticker} @ $${t.price.toFixed(2)}`}</span>
                <time dateTime={t.occurredUtc}>
                  {new Date(t.occurredUtc).toLocaleString()}
                </time>
              </li>
            ))}
          </ul>
        )}
        {hasMore && (
          <Button variant="ghost" onClick={loadMore} disabled={isPending}>
            Load more
          </Button>
        )}
      </Panel>
    </section>
  );
}
```

Add to `PortfolioScreen.module.css`:

```css
.sectionHeading {
  margin: 0 0 var(--mp-space-2);
  font-size: 1rem;
}

.historyList {
  margin: 0;
  padding: 0;
  list-style: none;
}

.historyItem {
  display: flex;
  justify-content: space-between;
  gap: var(--mp-space-2);
  padding: var(--mp-space-2) 0;
  border-bottom: 1px solid var(--mp-border-subtle);
}

.historyItem:last-child {
  border-bottom: none;
}

.historyItem time {
  color: var(--mp-text-muted);
  font-size: 0.8125rem;
  white-space: nowrap;
}

.empty {
  margin: 0;
  color: var(--mp-text-muted);
}
```

(Task 4's `HoldingsTable` heading style and this `sectionHeading` may unify — if Task 4 defined `.heading` in its own module, leave both; do not refactor across modules.)

Mount `<TransactionHistory />` last in `PortfolioScreen`.

- [ ] **Step 3: Run the full dashboard suite and build**

Run: `pnpm --filter @marketpulse/dashboard test -- --run && pnpm --filter @marketpulse/dashboard build`
Expected: PASS / clean.

- [ ] **Step 4: Commit**

```bash
git add apps/dashboard/src/features/portfolio
git commit -m "feat(dashboard): transaction history with load-more; portfolio screen complete"
```

---

### Task 7: Playwright journey

**Files:**
- Create: `tests/e2e/specs/portfolio.spec.ts`

**Interfaces:**
- Consumes: the UI contract copy (Global Constraints); the existing harness (API + dashboard + worker already boot; compose infra required).

- [ ] **Step 1: Write the journey**

```ts
import { expect, test } from '@playwright/test';

const PASSWORD = 'correct horse battery staple';

function uniqueEmail(): string {
  return `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@marketpulse.local`;
}

test('a buy and a higher sell produce correct holdings, P&L, and history', async ({
  page,
}) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  await page.getByRole('link', { name: 'Portfolio' }).click();
  await expect(page.getByRole('heading', { name: 'Portfolio' })).toBeVisible();
  await expect(page.getByText('No holdings yet. Record your first trade below.')).toBeVisible();

  // Buy 10 IVV @ 60 — user-typed prices make every P&L assertion deterministic.
  await page.getByLabel('Ticker').fill('IVV');
  await page.getByLabel('Side').selectOption('Buy');
  await page.getByLabel('Units').fill('10');
  await page.getByLabel('Price').fill('60');
  await page.getByRole('button', { name: 'Record trade' }).click();

  const row = page.getByRole('row', { name: /IVV/ });
  await expect(row).toContainText('10');
  await expect(row).toContainText('$60.00'); // average cost

  // Sell 4 @ 70 → realised 4 × (70 − 60) = +$40; 6 units remain at avg 60.
  await page.getByLabel('Ticker').fill('IVV');
  await page.getByLabel('Side').selectOption('Sell');
  await page.getByLabel('Units').fill('4');
  await page.getByLabel('Price').fill('70');
  await page.getByRole('button', { name: 'Record trade' }).click();

  await expect(row).toContainText('6');
  await expect(row).toContainText('+$40.00');

  // The live price cell fills from the stream — same tolerant first-tick assertion as
  // the watchlist journey; the P&L numbers above never depended on it.
  await expect(page.getByLabel('IVV price')).toHaveText(/^\$\d/, { timeout: 15_000 });

  // History, newest first.
  const items = page.getByRole('listitem');
  await expect(items.first()).toContainText('Sell 4 IVV @ $70.00');
  await expect(items.nth(1)).toContainText('Buy 10 IVV @ $60.00');

  // Everything survives a reload — server truth, not client state.
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Portfolio' })).toBeVisible();
  await expect(page.getByRole('row', { name: /IVV/ })).toContainText('+$40.00');
  await expect(page.getByRole('listitem').first()).toContainText('Sell 4 IVV @ $70.00');
});
```

(If `getByRole('row', { name: /IVV/ })` is ambiguous against the footer row, tighten to the data row via `page.getByRole('row').filter({ hasText: 'IVV' }).first()` — prefer whichever locator is strict-mode clean.)

- [ ] **Step 2: Run the e2e suite**

Prerequisite: `docker compose up -d` healthy.

Run: `pkill -f MarketPulse.Alerts; pnpm --dir tests/e2e exec playwright test`
Expected: all specs pass — `authentication.spec.ts` and `alerts.spec.ts` unchanged and green beside the new journey.

- [ ] **Step 3: Commit**

```bash
git add tests/e2e
git commit -m "test(e2e): portfolio journey — buy, sell, P&L, history, reload"
```

---

### Task 8: Docs

**Files:**
- Modify: `README.md`, `docs/MarketPulse-Pro-README.md` (portfolio feature rows: the dashboard surface now exists)
- Modify: `docs/ROADMAP.md` (5b done; phase 4 closes; "Verified against" anchor refreshed)
- Modify: `docs/TESTING.md` (portfolio frontend suites + journey)
- Modify: `docs/adr/007-state-architecture.md` (one-paragraph addendum)

- [ ] **Step 1: Edit**

- ADR-007 addendum (match the ADR's voice; append near its examples): unrealised P&L as the third worked example — server truth (`['portfolio']` cache) joined to the live stream at render; the em-dash rule for missing prices; still no second state layer.
- READMEs: `grep -n -i "portfolio" README.md docs/MarketPulse-Pro-README.md` — every claim about the portfolio UI/dashboard now true; the feature-table row names the live unrealised P&L derivation and the idempotency-key-sending form.
- ROADMAP: 5b marked done (one-line summary: route + nav, live unrealised P&L, key discipline, journey); phase-4 row → Done; "Verified against commit" anchor updated to the branch head at commit time.
- TESTING.md: the new Vitest suites (key-lifecycle assertion called out — it's the part 5a's server tests cannot see) and the journey.

- [ ] **Step 2: Verify and commit**

Re-read edited sections for consistency; run the grep and confirm every hit.

```bash
git add docs README.md
git commit -m "docs: portfolio dashboard lands — reconcile README, roadmap, testing; ADR-007 addendum"
```

---

### Task 9: Full verification

- [ ] **Step 1: Clean-slate build and test, every suite**

```bash
pnpm -r build && pnpm -r test -- --run
dotnet build MarketPulse.sln && dotnet test
pkill -f MarketPulse.Alerts 2>/dev/null; pnpm --dir tests/e2e exec playwright test
```

Expected: everything green — the .NET suites are the untouched-backend regression proof.

- [ ] **Step 2: Spec done-criteria walkthrough**

Check the spec's five done criteria against captured evidence (superpowers:verification-before-completion), naming the test that proves each — especially criterion 3 (every POST carries a key; retry reuses; new submission doesn't) and criterion 4 (no unrealised-P&L field in any schema; derivation tested). `git log --format=%B test..HEAD | grep -ic co-authored` → 0.

- [ ] **Step 3: Finish the branch**

Use superpowers:finishing-a-development-branch — merge into `test` per the promotion flow.
