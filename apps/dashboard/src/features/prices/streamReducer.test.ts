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

    expect(next.prices['IVV']).toEqual({
      price: 62.4,
      receivedAt: 1000,
      direction: 'neutral',
      seq: 1,
    });
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

  it('marks the first tick for a ticker as neutral', () => {
    const next = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    expect(next.prices['IVV']?.direction).toBe('neutral');
    expect(next.prices['IVV']?.seq).toBe(1);
  });

  it('marks a higher price as up', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 62.9, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.direction).toBe('up');
    expect(second.prices['IVV']?.seq).toBe(2);
  });

  it('marks a lower price as down', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 61.0, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.direction).toBe('down');
  });

  it('marks an unchanged price as neutral rather than implying movement', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.direction).toBe('neutral');
    expect(second.prices['IVV']?.seq).toBe(2);
  });

  it('tracks direction per ticker independently', () => {
    let state = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    state = streamReducer(state, {
      type: 'tick', ticker: 'NDQ', price: 30.0, receivedAt: 1000,
    });
    state = streamReducer(state, {
      type: 'tick', ticker: 'IVV', price: 63.0, receivedAt: 2000,
    });

    expect(state.prices['IVV']?.direction).toBe('up');
    expect(state.prices['NDQ']?.direction).toBe('neutral');
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
