import { describe, expect, it } from 'vitest';
import { tickSchema, watchlistSchema } from './schemas';

describe('watchlistSchema', () => {
  it('accepts a well-formed watchlist', () => {
    const parsed = watchlistSchema.parse({
      id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
      items: [{ ticker: 'IVV', addedUtc: '2026-07-31T00:00:00+00:00' }],
    });

    expect(parsed.items[0]?.ticker).toBe('IVV');
  });

  it('rejects a payload missing items', () => {
    expect(() => watchlistSchema.parse({ id: 'x' })).toThrow();
  });
});

describe('tickSchema', () => {
  it('accepts a well-formed tick', () => {
    const parsed = tickSchema.parse({
      ticker: 'NDQ',
      price: 54.31,
      timestampUtc: '2026-07-31T00:00:01+00:00',
    });

    expect(parsed.price).toBeCloseTo(54.31);
  });

  it('rejects a tick whose price is a string', () => {
    expect(() =>
      tickSchema.parse({ ticker: 'NDQ', price: '54.31', timestampUtc: 'x' }),
    ).toThrow();
  });
});
