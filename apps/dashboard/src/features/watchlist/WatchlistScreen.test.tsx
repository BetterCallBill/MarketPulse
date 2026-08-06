import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { MemoryRouter } from 'react-router-dom';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { WatchlistScreen } from './WatchlistScreen';

vi.mock('../prices/usePriceStream', () => ({
  usePriceStream: () => ({ status: 'connected' as const, prices: {} }),
}));

const server = setupServer(
  http.get('http://localhost:5100/api/v1/watchlist', () =>
    HttpResponse.json({
      id: 'w1',
      items: [{ ticker: 'IVV', addedUtc: '2026-07-31T00:00:00+00:00' }],
    }),
  ),
  http.get('http://localhost:5100/api/v1/alerts', () => HttpResponse.json([])),
  http.get('http://localhost:5100/api/v1/prices/sparklines', () =>
    HttpResponse.json({ sparklines: { IVV: [100.5, 101.25, 100.75] } }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <WatchlistScreen />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('WatchlistScreen', () => {
  it('renders the tickers returned by the API', async () => {
    renderScreen();

    expect(await screen.findByText('IVV')).toBeInTheDocument();
  });

  it('shows the server message when adding a duplicate', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/watchlist/items', () =>
        HttpResponse.json(
          {
            title: 'duplicate-ticker',
            status: 409,
            detail: "'IVV' is already on the watchlist.",
            correlationId: 'abc-123',
          },
          { status: 409 },
        ),
      ),
    );

    renderScreen();
    await screen.findByText('IVV');

    await userEvent.type(screen.getByLabelText(/add ticker/i), 'IVV');
    await userEvent.click(screen.getByRole('button', { name: /add/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(
        "'IVV' is already on the watchlist.",
      ),
    );
  });

  it('links the ticker to its price-history page', async () => {
    renderScreen();

    const link = await screen.findByRole('link', { name: 'IVV price history' });
    expect(link).toHaveAttribute('href', '/prices/IVV');
  });

  it('renders a sparkline from the batch endpoint', async () => {
    const { container } = renderScreen();

    await waitFor(() => expect(container.querySelector('polyline')).not.toBeNull());
  });
});
