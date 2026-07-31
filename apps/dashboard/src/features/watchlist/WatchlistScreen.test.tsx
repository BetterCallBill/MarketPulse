import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { WatchlistScreen } from './WatchlistScreen';

const server = setupServer(
  http.get('http://localhost:5100/api/v1/watchlist', () =>
    HttpResponse.json({
      id: 'w1',
      items: [{ ticker: 'IVV', addedUtc: '2026-07-31T00:00:00+00:00' }],
    }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <WatchlistScreen />
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
});
