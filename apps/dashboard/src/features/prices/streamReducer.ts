export const STALE_AFTER_MS = 10_000;

export type StreamStatus = 'connecting' | 'connected' | 'reconnecting';

export interface StreamState {
  status: StreamStatus;
  prices: Record<string, { price: number; receivedAt: number }>;
}

export type StreamAction =
  | { type: 'tick'; ticker: string; price: number; receivedAt: number }
  | { type: 'connected' }
  | { type: 'reconnecting' };

export const initialStreamState: StreamState = { status: 'connecting', prices: {} };

export function streamReducer(state: StreamState, action: StreamAction): StreamState {
  switch (action.type) {
    case 'tick':
      return {
        ...state,
        prices: {
          ...state.prices,
          [action.ticker]: { price: action.price, receivedAt: action.receivedAt },
        },
      };
    case 'connected':
      return { ...state, status: 'connected' };
    case 'reconnecting':
      return { ...state, status: 'reconnecting' };
    default: {
      const exhaustive: never = action;
      return exhaustive;
    }
  }
}

export function isStale(state: StreamState, ticker: string, now: number): boolean {
  const entry = state.prices[ticker];
  if (entry === undefined) return true;

  return now - entry.receivedAt >= STALE_AFTER_MS;
}
