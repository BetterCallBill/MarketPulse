import { memo } from 'react';
import type { TickDirection } from './streamReducer';
import styles from './PriceCell.module.css';

const ARROWS: Record<TickDirection, string> = { up: '▲', down: '▼', neutral: '' };

interface PriceCellProps {
  ticker: string;
  price: number | undefined;
  stale: boolean;
  disconnected: boolean;
  direction?: TickDirection;
  seq?: number;
}

export const PriceCell = memo(function PriceCell({
  ticker,
  price,
  stale,
  disconnected,
  direction = 'neutral',
  seq = 0,
}: PriceCellProps) {
  const dimmed = stale || disconnected;

  return (
    <span className={styles.cell} data-direction={direction}>
      {direction !== 'neutral' && (
        // Keyed on seq so React remounts this leaf on every tick, restarting the CSS
        // animation deterministically. aria-hidden: the arrow is decoration, and the
        // labelled price element below is what assistive technology reads.
        <span key={`arrow-${seq}`} className={styles.arrow} aria-hidden="true">
          {ARROWS[direction]}
        </span>
      )}
      {/*
       * Also keyed on seq: two consecutive same-direction ticks share an animation-name
       * (flash-up/flash-down), and a browser won't restart an animation on an element that
       * never remounts just because its class stays applied. Remounting only ever coincides
       * with a fresh tick, so it can't clip the stale-opacity transition below — staleness
       * sets in when ticks stop, which is exactly when seq (and this key) stop changing.
       * Namespaced (rather than sharing the arrow's key) because the two spans are siblings
       * and React requires keys to be unique only among siblings, but a shared raw seq key
       * still logs a duplicate-key warning.
       */}
      <span
        key={`price-${seq}`}
        className={styles.price}
        aria-label={`${ticker} price`}
        data-stale={String(dimmed)}
        title={disconnected ? 'Reconnecting…' : stale ? 'No recent update' : undefined}
      >
        {price === undefined ? '—' : `$${price.toFixed(2)}`}
      </span>
    </span>
  );
});
