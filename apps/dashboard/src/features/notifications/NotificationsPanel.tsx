import type { Notification } from '@marketpulse/api-client';
import { Panel } from '@marketpulse/ui';
import { useEffect } from 'react';
import styles from './Notifications.module.css';
import { useMarkRead } from './useNotifications';

export interface NotificationsPanelProps {
  notifications: Notification[];
}

export function NotificationsPanel({ notifications }: NotificationsPanelProps) {
  const markRead = useMarkRead();

  // Seen is read: opening the panel is the acknowledgement. Mount-only on purpose —
  // a row that arrives while the panel is already open stays unread until reopen,
  // which is also what keeps this from re-posting on every cache change.
  useEffect(() => {
    for (const n of notifications.filter((n) => !n.isRead)) {
      markRead.mutate(n.id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Panel className={styles.panel}>
      <h2 className={styles.heading}>Notifications</h2>
      {notifications.length === 0 ? (
        <p className={styles.empty}>Nothing yet. Alerts you set will land here when they fire.</p>
      ) : (
        <ul className={styles.list}>
          {notifications.map((n) => (
            <li key={n.id} className={styles.item} data-read={n.isRead}>
              <span>
                {`${n.ticker} crossed ${n.direction.toLowerCase()} $${n.threshold.toFixed(2)} — $${n.triggeredPrice.toFixed(2)}`}
              </span>
              <time className={styles.time} dateTime={n.occurredUtc}>
                {new Date(n.occurredUtc).toLocaleTimeString()}
              </time>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
