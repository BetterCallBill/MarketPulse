// "Stale" means "later than the slowest expected source cadence would explain," not a
// fixed UI taste — it is coupled to MarketDataOptions.PollInterval on the backend (60s
// default against Yahoo, ADR-010). 3x the slowest expected poll absorbs one missed poll
// without flashing fresh->stale on schedule, while still catching a genuinely dead feed
// well before a human would. See ADR-010's staleness-coupling consequence.
export const STALE_AFTER_MS = 180_000;

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
