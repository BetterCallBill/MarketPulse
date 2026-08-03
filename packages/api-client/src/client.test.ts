import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, createApiClient } from './client';

const BASE = 'http://api.test';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('createApiClient', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    // The CSRF cookie is readable by JavaScript by design — that is the "double submit".
    vi.stubGlobal('document', { cookie: 'mp_csrf=nonce-123' });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends credentials so the httpOnly cookies travel', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ id: 'w1', items: [] }));

    await createApiClient(BASE).getWatchlist();

    expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ credentials: 'include' });
  });

  it('attaches the CSRF header to mutations', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ id: 'w1', items: [] }));

    await createApiClient(BASE).addItem('IVV');

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    expect((init.headers as Record<string, string>)['X-CSRF-Token']).toBe('nonce-123');
  });

  it('does not attach the CSRF header to reads', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ id: 'w1', items: [] }));

    await createApiClient(BASE).getWatchlist();

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    expect((init.headers as Record<string, string>)['X-CSRF-Token']).toBeUndefined();
  });

  it('refreshes once and retries when a request returns 401', async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: 'unauthenticated', status: 401 }, 401))
      .mockResolvedValueOnce(jsonResponse({ id: 'u1', email: 'a@b.com' })) // refresh
      .mockResolvedValueOnce(jsonResponse({ id: 'w1', items: [] })); // retry

    const watchlist = await createApiClient(BASE).getWatchlist();

    expect(watchlist.items).toEqual([]);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[1]?.[0]).toBe(`${BASE}/api/v1/auth/refresh`);
  });

  it('gives up after one failed refresh rather than looping', async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: 'unauthenticated', status: 401 }, 401))
      .mockResolvedValueOnce(jsonResponse({ title: 'session-revoked', status: 401 }, 401));

    const client = createApiClient(BASE);

    await expect(client.getWatchlist()).rejects.toBeInstanceOf(ApiError);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('refreshes only once for concurrent 401s', async () => {
    // Three parallel requests hitting an expired token must produce ONE refresh call,
    // not three. This is what the single-flight promise is for.
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith('/auth/refresh')) {
        return Promise.resolve(jsonResponse({ id: 'u1', email: 'a@b.com' }));
      }
      if (fetchMock.mock.calls.filter((c) => !String(c[0]).endsWith('/auth/refresh')).length <= 3) {
        return Promise.resolve(jsonResponse({ title: 'unauthenticated', status: 401 }, 401));
      }
      return Promise.resolve(jsonResponse({ id: 'w1', items: [] }));
    });

    const client = createApiClient(BASE);
    await Promise.all([client.getWatchlist(), client.getWatchlist(), client.getWatchlist()]);

    const refreshCalls = fetchMock.mock.calls.filter((c) =>
      String(c[0]).endsWith('/auth/refresh'),
    );
    expect(refreshCalls).toHaveLength(1);
  });

  it('does not try to refresh a failed login', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ title: 'invalid-credentials', status: 401 }, 401),
    );

    await expect(
      createApiClient(BASE).login('a@b.com', 'wrong password here'),
    ).rejects.toBeInstanceOf(ApiError);

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('surfaces the problem details title as the error code', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        { title: 'duplicate-ticker', status: 409, detail: 'nope', correlationId: 'abc' },
        409,
      ),
    );

    await expect(createApiClient(BASE).addItem('IVV')).rejects.toMatchObject({
      status: 409,
      errorCode: 'duplicate-ticker',
      correlationId: 'abc',
    });
  });
});
