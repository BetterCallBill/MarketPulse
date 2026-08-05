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
