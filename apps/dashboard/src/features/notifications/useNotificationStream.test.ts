import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Notification } from '@marketpulse/api-client';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { createElement } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { notificationsKey } from './useNotifications';
import { useNotificationStream } from './useNotificationStream';

const handlers = new Map<string, (payload: unknown) => void>();

const mockConnection = {
  on: vi.fn((event: string, handler: (payload: unknown) => void) => {
    handlers.set(event, handler);
  }),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
  start: vi.fn().mockResolvedValue(undefined),
  stop: vi.fn().mockResolvedValue(undefined),
  state: 'Disconnected',
};

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: vi.fn().mockImplementation(() => ({
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    configureLogging: vi.fn().mockReturnThis(),
    build: vi.fn(() => mockConnection),
  })),
  HubConnectionState: { Disconnected: 'Disconnected' },
  LogLevel: { Warning: 0 },
}));

const push = {
  id: 'n1',
  alertRuleId: 'a1',
  ticker: 'IVV',
  direction: 'Above',
  threshold: 60,
  triggeredPrice: 61.2,
  occurredUtc: '2026-08-05T00:00:00+00:00',
};

describe('useNotificationStream', () => {
  afterEach(() => {
    vi.clearAllMocks();
    handlers.clear();
  });

  function renderStream() {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData<Notification[]>(notificationsKey, []);

    renderHook(() => useNotificationStream(true), {
      wrapper: ({ children }: { children: ReactNode }) =>
        createElement(QueryClientProvider, { client }, children),
    });

    return client;
  }

  it('prepends a pushed notification into the cache as unread', async () => {
    const client = renderStream();

    await waitFor(() => expect(handlers.has('notification')).toBe(true));
    handlers.get('notification')!(push);

    const cached = client.getQueryData<Notification[]>(notificationsKey);
    expect(cached?.[0]).toMatchObject({ id: 'n1', isRead: false });
  });

  it('ignores a payload that does not parse', async () => {
    const client = renderStream();

    await waitFor(() => expect(handlers.has('notification')).toBe(true));
    handlers.get('notification')!({ nonsense: true });

    expect(client.getQueryData<Notification[]>(notificationsKey)).toEqual([]);
  });

  it('does not open a connection when disabled', () => {
    const client = new QueryClient();

    renderHook(() => useNotificationStream(false), {
      wrapper: ({ children }: { children: ReactNode }) =>
        createElement(QueryClientProvider, { client }, children),
    });

    expect(mockConnection.start).not.toHaveBeenCalled();
  });
});
