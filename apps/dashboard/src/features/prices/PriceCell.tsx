import { memo } from 'react';

interface PriceCellProps {
  ticker: string;
  price: number | undefined;
  stale: boolean;
  disconnected: boolean;
}

export const PriceCell = memo(function PriceCell({
  ticker,
  price,
  stale,
  disconnected,
}: PriceCellProps) {
  const dimmed = stale || disconnected;

  return (
    <span
      aria-label={`${ticker} price`}
      style={{ opacity: dimmed ? 0.4 : 1 }}
      title={disconnected ? 'Reconnecting…' : stale ? 'No recent update' : undefined}
    >
      {price === undefined ? '—' : `$${price.toFixed(2)}`}
    </span>
  );
});
