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
      // A 401 on /me triggers the api-client's refresh-and-retry; give it a handler
      // too, so the request isn't unhandled.
      http.post('http://localhost:5100/api/v1/auth/refresh', () =>
        HttpResponse.json({ title: 'session-revoked', status: 401 }, { status: 401 }),
      ),
    );

    const { container } = renderNav();

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
