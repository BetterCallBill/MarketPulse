export const STALE_AFTER_MS = 10_000;

export type StreamStatus = 'connecting' | 'connected' | 'reconnecting';

export type TickDirection = 'up' | 'down' | 'neutral';

export interface PriceEntry {
  price: number;
  receivedAt: number;
  direction: TickDirection;
  /** Increments per tick. Exists so the flash animation can be restarted deterministically. */
  seq: number;
}

export interface StreamState {
  status: StreamStatus;
  prices: Record<string, PriceEntry>;
}

export type StreamAction =
  | { type: 'tick'; ticker: string; price: number; receivedAt: number }
  | { type: 'connected' }
  | { type: 'reconnecting' };

export const initialStreamState: StreamState = { status: 'connecting', prices: {} };

export function streamReducer(state: StreamState, action: StreamAction): StreamState {
  switch (action.type) {
    case 'tick': {
      const previous = state.prices[action.ticker];

      // A first tick has nothing to compare against, and an unchanged price did not
      // move — neither should flash, because a flash asserts movement.
      const direction: TickDirection =
        previous === undefined || action.price === previous.price
          ? 'neutral'
          : action.price > previous.price
            ? 'up'
            : 'down';

      return {
        ...state,
        prices: {
          ...state.prices,
          [action.ticker]: {
            price: action.price,
            receivedAt: action.receivedAt,
            direction,
            seq: (previous?.seq ?? 0) + 1,
          },
        },
      };
    }
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
