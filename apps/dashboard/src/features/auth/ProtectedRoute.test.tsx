import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { ProtectedRoute } from './ProtectedRoute';

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderAt(path: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/login" element={<p>Sign in page</p>} />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <p>Secret watchlist</p>
              </ProtectedRoute>
            }
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ProtectedRoute', () => {
  it('renders the children when the session is valid', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/auth/me', () =>
        HttpResponse.json({ id: 'u1', email: 'someone@marketpulse.local' }),
      ),
    );

    renderAt('/');

    expect(await screen.findByText('Secret watchlist')).toBeInTheDocument();
  });

  it('redirects to the login page when the session is missing', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/auth/me', () =>
        HttpResponse.json({ title: 'unauthenticated', status: 401 }, { status: 401 }),
      ),
      http.post('http://localhost:5100/api/v1/auth/refresh', () =>
        HttpResponse.json({ title: 'session-revoked', status: 401 }, { status: 401 }),
      ),
    );

    renderAt('/');

    expect(await screen.findByText('Sign in page')).toBeInTheDocument();
  });
});
