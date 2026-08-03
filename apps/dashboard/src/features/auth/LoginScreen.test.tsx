import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { MemoryRouter } from 'react-router-dom';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { LoginScreen } from './LoginScreen';

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <LoginScreen />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('LoginScreen', () => {
  it('shows the server message when the credentials are wrong', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/auth/login', () =>
        HttpResponse.json(
          {
            title: 'invalid-credentials',
            status: 401,
            detail: 'Email or password is incorrect.',
          },
          { status: 401 },
        ),
      ),
    );

    renderScreen();

    await userEvent.type(screen.getByLabelText(/email/i), 'someone@marketpulse.local');
    await userEvent.type(screen.getByLabelText(/password/i), 'wrong password here');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('Email or password is incorrect.'),
    );
  });

  it('tells the user how long to wait when the account is locked', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/auth/login', () =>
        HttpResponse.json(
          {
            title: 'account-locked',
            status: 429,
            detail: 'Too many failed attempts. Try again later.',
          },
          { status: 429 },
        ),
      ),
    );

    renderScreen();

    await userEvent.type(screen.getByLabelText(/email/i), 'someone@marketpulse.local');
    await userEvent.type(screen.getByLabelText(/password/i), 'wrong password here');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/too many failed attempts/i),
    );
  });
});
