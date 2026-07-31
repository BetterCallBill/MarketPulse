import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { usePriceStream } from './usePriceStream';

const mockConnection = {
  on: vi.fn(),
  onreconnecting: vi.fn(),
  onreconnected: vi.fn(),
  onclose: vi.fn(),
  start: vi.fn(),
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

describe('usePriceStream', () => {
  afterEach(() => {
    vi.clearAllMocks();
    mockConnection.state = 'Disconnected';
  });

  it('moves to reconnecting, not stuck on connecting, when the initial connection fails', async () => {
    mockConnection.start.mockRejectedValue(new Error('hub unreachable'));

    const { result } = renderHook(() => usePriceStream());

    expect(result.current.status).toBe('connecting');

    await waitFor(() => {
      expect(result.current.status).toBe('reconnecting');
    });
  });

  it('reaches connected when the initial connection succeeds', async () => {
    mockConnection.start.mockResolvedValue(undefined);

    const { result } = renderHook(() => usePriceStream());

    await waitFor(() => {
      expect(result.current.status).toBe('connected');
    });
  });
});
