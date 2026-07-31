import { describe, expect, it } from 'vitest';
import { initialStreamState, isStale, streamReducer, STALE_AFTER_MS } from './streamReducer';

describe('streamReducer', () => {
  it('starts in the connecting state with no prices', () => {
    expect(initialStreamState.status).toBe('connecting');
    expect(initialStreamState.prices).toEqual({});
  });

  it('records a tick against its ticker', () => {
    const next = streamReducer(initialStreamState, {
      type: 'tick',
      ticker: 'IVV',
      price: 62.4,
      receivedAt: 1000,
    });

    expect(next.prices['IVV']).toEqual({ price: 62.4, receivedAt: 1000 });
  });

  it('overwrites an earlier price for the same ticker', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 62.9, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.price).toBe(62.9);
  });

  it('transitions to reconnecting but keeps the last known prices', () => {
    const withTick = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    const dropped = streamReducer(withTick, { type: 'reconnecting' });

    expect(dropped.status).toBe('reconnecting');
    expect(dropped.prices['IVV']?.price).toBe(62.4);
  });

  it('transitions back to connected', () => {
    const dropped = streamReducer(initialStreamState, { type: 'reconnecting' });

    expect(streamReducer(dropped, { type: 'connected' }).status).toBe('connected');
  });
});

describe('isStale', () => {
  it('is false immediately after a tick', () => {
    const state = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    expect(isStale(state, 'IVV', 1000 + STALE_AFTER_MS - 1)).toBe(false);
  });

  it('is true once the stale threshold elapses', () => {
    const state = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    expect(isStale(state, 'IVV', 1000 + STALE_AFTER_MS)).toBe(true);
  });

  it('treats an unseen ticker as stale', () => {
    expect(isStale(initialStreamState, 'NDQ', 5000)).toBe(true);
  });
});
