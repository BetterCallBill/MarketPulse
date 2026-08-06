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
