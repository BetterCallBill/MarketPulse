import styles from './Sparkline.module.css';

export interface SparklineProps {
  points: number[];
  width?: number;
  height?: number;
}

/**
 * A trend decoration, not a chart: no axes, no interaction, hidden from assistive
 * technology (the row's price cell already carries the accessible price). Fewer than two
 * points cannot describe a line, so nothing renders.
 */
export function Sparkline({ points, width = 96, height = 24 }: SparklineProps) {
  if (points.length < 2) return null;

  const min = Math.min(...points);
  const max = Math.max(...points);
  const range = max - min || 1; // flat series draws a flat line, not NaN
  const step = width / (points.length - 1);

  const path = points
    .map((p, i) => `${(i * step).toFixed(2)},${(height - ((p - min) / range) * height).toFixed(2)}`)
    .join(' ');

  return (
    <svg
      className={styles.sparkline}
      viewBox={`0 0 ${width} ${height}`}
      width={width}
      height={height}
      aria-hidden="true"
      focusable="false"
    >
      <polyline points={path} fill="none" />
    </svg>
  );
}
