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
