import { z } from 'zod';
import {
  alertRuleSchema,
  candlesSchema,
  notificationSchema,
  portfolioSchema,
  problemDetailsSchema,
  sessionSchema,
  sparklinesSchema,
  transactionSchema,
  watchlistSchema,
  type AlertDirection,
  type AlertRule,
  type CandleInterval,
  type Candles,
  type Notification,
  type Portfolio,
  type Session,
  type Sparklines,
  type Transaction,
  type TradeSide,
  type Watchlist,
} from './schemas';

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

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

/** The CSRF cookie is deliberately not httpOnly — reading it here is the "double" in double-submit. */
function readCsrfToken(): string | undefined {
  if (typeof document === 'undefined') return undefined;

  const match = document.cookie
    .split(';')
    .map((c) => c.trim())
    .find((c) => c.startsWith('mp_csrf='));

  return match ? decodeURIComponent(match.slice('mp_csrf='.length)) : undefined;
}

export function createApiClient(baseUrl: string) {
  const root = baseUrl.replace(/\/$/, '');

  /**
   * One in-flight refresh at a time. Ten components hitting an expired access token
   * simultaneously must produce one refresh call, not ten — and the nine that did not
   * initiate it still need to await the same result before retrying.
   */
  let refreshInFlight: Promise<boolean> | null = null;

  function refreshOnce(): Promise<boolean> {
    refreshInFlight ??= fetch(`${root}/api/v1/auth/refresh`, {
      method: 'POST',
      credentials: 'include',
      headers: csrfHeaders('POST'),
    })
      .then((response) => response.ok)
      .catch(() => false)
      .finally(() => {
        refreshInFlight = null;
      });

    return refreshInFlight;
  }

  function csrfHeaders(method: string): Record<string, string> {
    if (SAFE_METHODS.has(method.toUpperCase())) return {};

    const token = readCsrfToken();
    return token ? { 'X-CSRF-Token': token } : {};
  }

  async function request<T>(
    path: string,
    init: RequestInit,
    parse: (data: unknown) => T,
    allowRefresh = true,
  ): Promise<T> {
    const method = init.method ?? 'GET';

    const response = await fetch(`${root}${path}`, {
      ...init,
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        // Browser-minted correlation: the middleware honours inbound IDs, so ApiError's
        // correlationId now matches a searchable server-side trail end to end.
        'X-Correlation-Id': crypto.randomUUID(),
        ...csrfHeaders(method),
        ...init.headers,
      },
    });

    // An expired access token is the expected steady state every 15 minutes, so it is
    // handled here rather than surfaced to every caller.
    if (response.status === 401 && allowRefresh) {
      const refreshed = await refreshOnce();
      if (refreshed) {
        return request(path, init, parse, false);
      }
    }

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

  return {
    register: (email: string, password: string): Promise<Session> =>
      request(
        '/api/v1/auth/register',
        { method: 'POST', body: JSON.stringify({ email, password }) },
        (d) => sessionSchema.parse(d),
        false,
      ),

    // allowRefresh is false: a 401 from login means the credentials are wrong, and
    // trying to refresh in response would be nonsense.
    login: (email: string, password: string): Promise<Session> =>
      request(
        '/api/v1/auth/login',
        { method: 'POST', body: JSON.stringify({ email, password }) },
        (d) => sessionSchema.parse(d),
        false,
      ),

    logout: (): Promise<void> =>
      request('/api/v1/auth/logout', { method: 'POST' }, () => undefined, false),

    me: (signal?: AbortSignal): Promise<Session> =>
      request('/api/v1/auth/me', { method: 'GET', signal }, (d) => sessionSchema.parse(d)),

    getWatchlist: (signal?: AbortSignal): Promise<Watchlist> =>
      request('/api/v1/watchlist', { method: 'GET', signal }, (d) => watchlistSchema.parse(d)),

    addItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        '/api/v1/watchlist/items',
        { method: 'POST', body: JSON.stringify({ ticker }), signal },
        (d) => watchlistSchema.parse(d),
      ),

    removeItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        `/api/v1/watchlist/items/${encodeURIComponent(ticker)}`,
        { method: 'DELETE', signal },
        (d) => watchlistSchema.parse(d),
      ),

    getAlerts: (signal?: AbortSignal): Promise<AlertRule[]> =>
      request('/api/v1/alerts', { method: 'GET', signal }, (d) =>
        z.array(alertRuleSchema).parse(d),
      ),

    createAlert: (
      ticker: string,
      direction: AlertDirection,
      threshold: number,
      signal?: AbortSignal,
    ): Promise<AlertRule> =>
      request(
        '/api/v1/alerts',
        { method: 'POST', body: JSON.stringify({ ticker, direction, threshold }), signal },
        (d) => alertRuleSchema.parse(d),
      ),

    deleteAlert: (id: string, signal?: AbortSignal): Promise<void> =>
      request(
        `/api/v1/alerts/${encodeURIComponent(id)}`,
        { method: 'DELETE', signal },
        () => undefined,
      ),

    rearmAlert: (id: string, signal?: AbortSignal): Promise<AlertRule> =>
      request(
        `/api/v1/alerts/${encodeURIComponent(id)}/rearm`,
        { method: 'POST', signal },
        (d) => alertRuleSchema.parse(d),
      ),

    getNotifications: (signal?: AbortSignal): Promise<Notification[]> =>
      request('/api/v1/notifications', { method: 'GET', signal }, (d) =>
        z.array(notificationSchema).parse(d),
      ),

    // keepalive: true is deliberate and scoped to this one call — it's the "must
    // survive navigation" request (the user closing the tab right after acknowledging
    // a notification shouldn't leave it unread), not a default every mutation needs.
    markNotificationRead: (id: string, signal?: AbortSignal): Promise<void> =>
      request(
        `/api/v1/notifications/${encodeURIComponent(id)}/read`,
        { method: 'POST', signal, keepalive: true },
        () => undefined,
      ),

    getPortfolio: (signal?: AbortSignal): Promise<Portfolio> =>
      request('/api/v1/portfolio', { method: 'GET', signal }, (d) =>
        portfolioSchema.parse(d),
      ),

    getTransactions: (skip: number, take: number, signal?: AbortSignal): Promise<Transaction[]> =>
      request(
        `/api/v1/portfolio/transactions?skip=${skip}&take=${take}`,
        { method: 'GET', signal },
        (d) => z.array(transactionSchema).parse(d),
      ),

    recordTransaction: (
      trade: { ticker: string; side: TradeSide; units: number; price: number },
      idempotencyKey: string,
      signal?: AbortSignal,
    ): Promise<Portfolio> =>
      request(
        '/api/v1/portfolio/transactions',
        {
          method: 'POST',
          body: JSON.stringify(trade),
          headers: { 'Idempotency-Key': idempotencyKey },
          signal,
        },
        (d) => portfolioSchema.parse(d),
      ),

    getCandles: (
      ticker: string,
      interval: CandleInterval,
      from: string,
      to: string,
      signal?: AbortSignal,
    ): Promise<Candles> =>
      request(
        `/api/v1/prices/${encodeURIComponent(ticker)}/candles?interval=${interval}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`,
        { method: 'GET', signal },
        (d) => candlesSchema.parse(d),
      ),

    getSparklines: (signal?: AbortSignal): Promise<Sparklines> =>
      request('/api/v1/prices/sparklines', { method: 'GET', signal }, (d) =>
        sparklinesSchema.parse(d),
      ),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
