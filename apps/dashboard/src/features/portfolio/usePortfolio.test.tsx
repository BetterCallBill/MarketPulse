import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
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
  // Real skip/take paging: each page request asks for its own slice, never a growing window.
  http.get('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
    const url = new URL(request.url);
    const skip = Number(url.searchParams.get('skip'));
    const take = Number(url.searchParams.get('take'));
    return HttpResponse.json([...page0, ...page1].slice(skip, skip + take));
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

    // loadMore synchronously bumps state outside an event handler here, so it
    // needs an explicit act() to keep React from warning about the update.
    act(() => {
      result.current.loadMore();
    });

    await waitFor(() => expect(result.current.transactions).toHaveLength(3));
    expect(result.current.transactions[2]?.id).toBe('t1');
    expect(result.current.hasMore).toBe(false); // short page ends it
  });

  it('never requests more than pageSize per page, no matter how many loadMores fire (cap-safety)', async () => {
    // The server rejects take > 100; a growing take=pages*pageSize window would hit that
    // cap on real histories. This proves each page request stays fixed at pageSize instead.
    const fixture = Array.from({ length: 5 }, (_, i) => ({
      id: `f${i}`,
      ticker: 'IVV',
      side: 'Buy',
      units: 1,
      price: 60,
      occurredUtc: `2026-08-05T0${i}:00:00+00:00`,
      recordedUtc: `2026-08-05T0${i}:00:00+00:00`,
    }));
    const seenTakes: number[] = [];
    server.use(
      http.get('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
        const url = new URL(request.url);
        const skip = Number(url.searchParams.get('skip'));
        const take = Number(url.searchParams.get('take'));
        seenTakes.push(take);
        return HttpResponse.json(fixture.slice(skip, skip + take));
      }),
    );

    const { Wrapper } = wrapper();
    const { result } = renderHook(() => useTransactions(2), { wrapper: Wrapper });

    await waitFor(() => expect(result.current.transactions).toHaveLength(2));

    await act(async () => {
      result.current.loadMore();
    });
    await waitFor(() => expect(result.current.transactions).toHaveLength(4));

    await act(async () => {
      result.current.loadMore();
    });
    await waitFor(() => expect(result.current.transactions).toHaveLength(5));

    await act(async () => {
      result.current.loadMore(); // history is drained; this must be a no-op, not a bigger take
    });

    expect(seenTakes.length).toBeGreaterThanOrEqual(3);
    expect(seenTakes.every((take) => take === 2)).toBe(true);
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
