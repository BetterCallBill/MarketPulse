import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { NotificationBell } from './NotificationBell';

// The stream is unit-tested on its own; here it must simply not open sockets.
vi.mock('./useNotificationStream', () => ({ useNotificationStream: () => undefined }));

const unread = {
  id: 'n1',
  alertRuleId: 'a1',
  ticker: 'IVV',
  direction: 'Above',
  threshold: 60,
  triggeredPrice: 61.2,
  occurredUtc: '2026-08-05T00:00:00+00:00',
  isRead: false,
};

// A distinct ticker/threshold from `unread` — sharing its text would make the row
// assertion below ambiguous (findByText requires a unique match).
const read = {
  ...unread,
  id: 'n2',
  ticker: 'BHP',
  threshold: 40,
  triggeredPrice: 39.5,
  direction: 'Below',
  isRead: true,
};

const server = setupServer(
  http.get('http://localhost:5100/api/v1/auth/me', () =>
    HttpResponse.json({ id: 'u1', email: 'billy@example.test' }),
  ),
  http.get('http://localhost:5100/api/v1/notifications', () =>
    HttpResponse.json([unread, read]),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderBell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <NotificationBell />
    </QueryClientProvider>,
  );
}

describe('NotificationBell', () => {
  it('shows the unread count, not the total', async () => {
    renderBell();

    const bell = await screen.findByRole('button', { name: /notifications/i });
    await waitFor(() => expect(bell).toHaveTextContent('1'));
  });

  it('opens the panel and marks the unread rows read', async () => {
    const readIds: string[] = [];
    server.use(
      http.post('http://localhost:5100/api/v1/notifications/:id/read', ({ params }) => {
        readIds.push(String(params['id']));
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderBell();

    await userEvent.click(await screen.findByRole('button', { name: /notifications/i }));

    expect(
      await screen.findByText('IVV crossed above $60.00 — $61.20'),
    ).toBeInTheDocument();

    // Only the unread row is posted; the read one is left alone. And the badge clears
    // optimistically — the unread count is derived from the cache, never stored.
    await waitFor(() => expect(readIds).toEqual(['n1']));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: /notifications/i })).not.toHaveTextContent('1'),
    );
  });

  it('renders nothing without a session', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/auth/me', () =>
        HttpResponse.json({ title: 'unauthorized', status: 401 }, { status: 401 }),
      ),
      // A 401 on /me triggers the api-client's refresh-and-retry; give it a handler
      // too, matching ProtectedRoute.test.tsx's convention, so the request isn't
      // unhandled.
      http.post('http://localhost:5100/api/v1/auth/refresh', () =>
        HttpResponse.json({ title: 'session-revoked', status: 401 }, { status: 401 }),
      ),
    );

    const { container } = renderBell();

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
