import styles from './StatusDot.module.css';

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting';

const LABELS: Record<ConnectionStatus, string> = {
  connecting: 'Connecting…',
  connected: 'Live',
  reconnecting: 'Reconnecting…',
};

export interface StatusDotProps {
  status: ConnectionStatus;
}

/** Colour is never the only signal: the text label carries the same meaning. */
export function StatusDot({ status }: StatusDotProps) {
  return (
    <span className={styles.status} data-status={status}>
      <span className={styles.dot} aria-hidden="true" />
      {LABELS[status]}
    </span>
  );
}
