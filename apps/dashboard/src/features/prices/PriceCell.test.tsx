import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PriceCell } from './PriceCell';
import { isStale, type StreamState } from './streamReducer';
import { useNow } from './useNow';

/**
 * Mirrors how WatchlistScreen wires the pieces together: a live `now` from
 * `useNow`, fed into the pure `isStale`, driving `PriceCell`'s dimming.
 * This proves the fix for the "frozen now" defect — a cell dims on its own,
 * with no further ticks and no unrelated re-render forcing it.
 */
function Harness({ state, ticker }: { state: StreamState; ticker: string }) {
  const now = useNow();

  return (
    <PriceCell
      ticker={ticker}
      price={state.prices[ticker]?.price}
      stale={isStale(state, ticker, now)}
      disconnected={state.status === 'reconnecting'}
    />
  );
}

describe('PriceCell live staleness', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('dims once the stale threshold elapses with no further ticks', () => {
    const receivedAt = Date.now();
    const state: StreamState = {
      status: 'connected',
      prices: { IVV: { price: 62.4, receivedAt } },
    };

    render(<Harness state={state} ticker="IVV" />);

    const cell = screen.getByLabelText('IVV price');
    expect(cell).toHaveStyle({ opacity: '1' });

    act(() => {
      vi.advanceTimersByTime(10_000);
    });

    expect(cell).toHaveStyle({ opacity: '0.4' });
    expect(cell).toHaveAttribute('title', 'No recent update');
  });
});
