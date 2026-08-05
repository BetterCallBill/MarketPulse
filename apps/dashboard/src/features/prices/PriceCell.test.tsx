import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PriceCell } from './PriceCell';
import { isStale, STALE_AFTER_MS, type StreamState } from './streamReducer';
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
      direction={state.prices[ticker]?.direction ?? 'neutral'}
      seq={state.prices[ticker]?.seq ?? 0}
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

  it('marks itself stale once the threshold elapses with no further ticks', () => {
    const receivedAt = Date.now();
    const state: StreamState = {
      status: 'connected',
      prices: { IVV: { price: 62.4, receivedAt, direction: 'neutral', seq: 1 } },
    };

    render(<Harness state={state} ticker="IVV" />);

    const cell = screen.getByLabelText('IVV price');
    expect(cell).toHaveAttribute('data-stale', 'false');

    act(() => {
      vi.advanceTimersByTime(STALE_AFTER_MS);
    });

    expect(cell).toHaveAttribute('data-stale', 'true');
    expect(cell).toHaveAttribute('title', 'No recent update');
  });
});

describe('PriceCell direction', () => {
  it('shows only the formatted price inside the labelled element', () => {
    render(
      <PriceCell ticker="IVV" price={62.4} stale={false} disconnected={false}
        direction="up" seq={2} />,
    );

    // E2E asserts /^\$\d/ against this element's text — an arrow inside it would break that.
    expect(screen.getByLabelText('IVV price')).toHaveTextContent(/^\$62\.40$/);
  });

  it('renders the direction arrow outside the labelled element and hides it', () => {
    const { container } = render(
      <PriceCell ticker="IVV" price={62.4} stale={false} disconnected={false}
        direction="up" seq={2} />,
    );

    const arrow = container.querySelector('[aria-hidden="true"]');
    expect(arrow).not.toBeNull();
    expect(screen.getByLabelText('IVV price')).not.toContainElement(arrow as HTMLElement);
  });

  it('renders no arrow for a neutral tick', () => {
    const { container } = render(
      <PriceCell ticker="IVV" price={62.4} stale={false} disconnected={false}
        direction="neutral" seq={1} />,
    );

    expect(container.querySelector('[aria-hidden="true"]')).toBeNull();
  });

  it('exposes the direction for styling', () => {
    const { container } = render(
      <PriceCell ticker="IVV" price={61.0} stale={false} disconnected={false}
        direction="down" seq={3} />,
    );

    expect(container.querySelector('[data-direction="down"]')).not.toBeNull();
  });

  it('renders an em dash before the first tick arrives', () => {
    render(
      <PriceCell ticker="IVV" price={undefined} stale disconnected={false}
        direction="neutral" seq={0} />,
    );

    expect(screen.getByLabelText('IVV price')).toHaveTextContent('—');
  });
});
