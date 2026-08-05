import { describe, expect, it } from 'vitest';
import {
  alertRuleSchema,
  notificationPushSchema,
  notificationSchema,
  tickSchema,
  watchlistSchema,
} from './schemas';

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

describe('alertRuleSchema', () => {
  it('parses a rule as the API serialises it', () => {
    const rule = alertRuleSchema.parse({
      id: 'a1',
      ticker: 'IVV',
      direction: 'Above',
      threshold: 65.5,
      status: 'Active',
      createdUtc: '2026-08-05T00:00:00+00:00',
      triggeredUtc: null,
      triggeredPrice: null,
    });

    expect(rule.direction).toBe('Above');
  });

  it('rejects a direction outside Above/Below', () => {
    expect(() =>
      alertRuleSchema.parse({
        id: 'a1',
        ticker: 'IVV',
        direction: 'Sideways',
        threshold: 65.5,
        status: 'Active',
        createdUtc: '2026-08-05T00:00:00+00:00',
        triggeredUtc: null,
        triggeredPrice: null,
      }),
    ).toThrow();
  });
});

describe('notification schemas', () => {
  it('parses an API row including its read flag', () => {
    const n = notificationSchema.parse({
      id: 'n1',
      alertRuleId: 'a1',
      ticker: 'IVV',
      direction: 'Above',
      threshold: 60,
      triggeredPrice: 61.2,
      occurredUtc: '2026-08-05T00:00:00+00:00',
      isRead: false,
    });

    expect(n.isRead).toBe(false);
  });

  it('parses a hub push, which carries no read flag', () => {
    const p = notificationPushSchema.parse({
      id: 'n1',
      alertRuleId: 'a1',
      ticker: 'IVV',
      direction: 'Above',
      threshold: 60,
      triggeredPrice: 61.2,
      occurredUtc: '2026-08-05T00:00:00+00:00',
    });

    expect(p.ticker).toBe('IVV');
  });
});
