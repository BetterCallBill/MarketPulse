import { VisuallyHidden } from '../VisuallyHidden/VisuallyHidden';
import styles from './Badge.module.css';

export interface BadgeProps {
  count: number;
  /** What the count means, e.g. "unread notifications". Read by screen readers. */
  label: string;
  /** Visible display caps here as "9+"; the accessible text keeps the real count. */
  max?: number;
}

/** Renders nothing at zero: an empty badge is noise, and its accessible name goes with it. */
export function Badge({ count, label, max = 9 }: BadgeProps) {
  if (count <= 0) return null;

  const display = count > max ? `${max}+` : String(count);

  return (
    <span className={styles.badge}>
      <span aria-hidden="true">{display}</span>
      <VisuallyHidden>{`${count} ${label}`}</VisuallyHidden>
    </span>
  );
}
