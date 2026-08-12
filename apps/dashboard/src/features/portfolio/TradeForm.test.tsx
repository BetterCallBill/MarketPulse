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

  it('offers Retry with the same key after a network failure (ambiguous outcome)', async () => {
    let calls = 0;
    server.use(
      http.post('http://localhost:5100/api/v1/portfolio/transactions', ({ request }) => {
        calls += 1;
        if (calls === 1) {
          seenKeys.push(request.headers.get('Idempotency-Key') ?? '(none)');
          return HttpResponse.error();
        }
        seenKeys.push(request.headers.get('Idempotency-Key') ?? '(none)');
        return HttpResponse.json({ holdings: [], totalRealisedPnL: 0 }, { status: 201 });
      }),
    );

    renderForm();

    await fillAndSubmit();
    await userEvent.click(await screen.findByRole('button', { name: 'Retry' }));

    // Same submission, so the retry after a network failure reuses the same key.
    await waitFor(() => expect(seenKeys).toHaveLength(2));
    expect(seenKeys[1]).toBe(seenKeys[0]);
  });
});
