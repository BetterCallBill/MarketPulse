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
        <span key={seq} className={styles.arrow} aria-hidden="true">
          {ARROWS[direction]}
        </span>
      )}
      <span
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
