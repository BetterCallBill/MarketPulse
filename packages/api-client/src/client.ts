import { problemDetailsSchema, watchlistSchema, type Watchlist } from './schemas';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly errorCode: string,
    message: string,
    readonly correlationId?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(
  url: string,
  init: RequestInit,
  parse: (data: unknown) => T,
): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init.headers },
  });

  const body: unknown = await response.json().catch(() => null);

  if (!response.ok) {
    const problem = problemDetailsSchema.safeParse(body);
    throw new ApiError(
      response.status,
      problem.success ? problem.data.title : 'unknown-error',
      problem.success ? (problem.data.detail ?? problem.data.title) : response.statusText,
      problem.success ? problem.data.correlationId : undefined,
    );
  }

  return parse(body);
}

export function createApiClient(baseUrl: string) {
  const root = baseUrl.replace(/\/$/, '');

  return {
    getWatchlist: (signal?: AbortSignal): Promise<Watchlist> =>
      request(`${root}/api/v1/watchlist`, { method: 'GET', signal }, (d) =>
        watchlistSchema.parse(d),
      ),

    addItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        `${root}/api/v1/watchlist/items`,
        { method: 'POST', body: JSON.stringify({ ticker }), signal },
        (d) => watchlistSchema.parse(d),
      ),

    removeItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        `${root}/api/v1/watchlist/items/${encodeURIComponent(ticker)}`,
        { method: 'DELETE', signal },
        (d) => watchlistSchema.parse(d),
      ),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
