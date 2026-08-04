import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { AlertCell } from './AlertCell';

const activeRule = {
  id: 'a1',
  ticker: 'IVV',
  direction: 'Above',
  threshold: 60,
  status: 'Active',
  createdUtc: '2026-08-05T00:00:00+00:00',
  triggeredUtc: null,
  triggeredPrice: null,
};

const server = setupServer(
  http.get('http://localhost:5100/api/v1/alerts', () => HttpResponse.json([])),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderCell(ticker = 'IVV') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AlertCell ticker={ticker} />
    </QueryClientProvider>,
  );
}

describe('AlertCell', () => {
  it('offers the create form when the ticker has no rule', async () => {
    renderCell();

    expect(await screen.findByLabelText('Alert threshold for IVV')).toBeInTheDocument();
    expect(screen.getByLabelText('Alert direction for IVV')).toBeInTheDocument();
  });

  it('flags an empty threshold as invalid instead of silently ignoring submit', async () => {
    let posted = false;
    server.use(
      http.post('http://localhost:5100/api/v1/alerts', () => {
        posted = true;
        return HttpResponse.json(activeRule, { status: 201 });
      }),
    );

    renderCell();

    const thresholdInput = await screen.findByLabelText('Alert threshold for IVV');
    await userEvent.click(screen.getByRole('button', { name: 'Set alert for IVV' }));

    expect(thresholdInput).toBeInvalid();
    expect(posted).toBe(false);
  });

  it('creates a rule and swaps to its status', async () => {
    // Stateful handlers: after the POST succeeds, the invalidation-triggered refetch
    // must return the new rule for the cell to swap from form to status.
    let posted: unknown;
    let created = false;
    server.use(
      http.get('http://localhost:5100/api/v1/alerts', () =>
        HttpResponse.json(created ? [activeRule] : []),
      ),
      http.post('http://localhost:5100/api/v1/alerts', async ({ request }) => {
        posted = await request.json();
        created = true;
        return HttpResponse.json(activeRule, { status: 201 });
      }),
    );

    renderCell();

    await userEvent.selectOptions(
      await screen.findByLabelText('Alert direction for IVV'),
      'Above',
    );
    await userEvent.type(screen.getByLabelText('Alert threshold for IVV'), '60');
    await userEvent.click(screen.getByRole('button', { name: 'Set alert for IVV' }));

    expect(await screen.findByText('Active')).toBeInTheDocument();
    expect(posted).toEqual({ ticker: 'IVV', direction: 'Above', threshold: 60 });
  });

  it('shows a triggered rule with a re-arm action', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/alerts', () =>
        HttpResponse.json([
          { ...activeRule, status: 'Triggered', triggeredPrice: 61.2 },
        ]),
      ),
    );

    renderCell();

    expect(await screen.findByText('Triggered')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Re-arm alert for IVV' })).toBeInTheDocument();
  });

  it('surfaces the server message when creation is rejected', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/alerts', () =>
        HttpResponse.json(
          { title: 'unknown-ticker', status: 400, detail: "'ZZZ' is not a known ticker." },
          { status: 400 },
        ),
      ),
    );

    renderCell('ZZZ');

    await userEvent.type(await screen.findByLabelText('Alert threshold for ZZZ'), '10');
    await userEvent.click(screen.getByRole('button', { name: 'Set alert for ZZZ' }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent("'ZZZ' is not a known ticker."),
    );
  });
});
