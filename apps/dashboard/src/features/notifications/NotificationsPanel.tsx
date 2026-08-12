import type { Notification } from '@marketpulse/api-client';
import { Panel } from '@marketpulse/ui';
import { useEffect, useRef } from 'react';
import styles from './Notifications.module.css';
import { useMarkRead } from './useNotifications';

export interface NotificationsPanelProps {
  notifications: Notification[];
  isPending: boolean;
}

export function NotificationsPanel({ notifications, isPending }: NotificationsPanelProps) {
  const markRead = useMarkRead();
  const hasRunRef = useRef(false);

  // Seen is read: opening the panel is the acknowledgement. Latched to the first
  // transition from loading to loaded, not bare mount — the panel can open before the
  // initial fetch resolves, and marking read against that empty pre-fetch array would
  // leave the rows that load a moment later sitting unread with the panel open. The
  // ref makes this a genuine run-once: a row that arrives while the panel is already
  // open (loaded) stays unread until reopen, which is also what keeps this from
  // re-posting on every later cache change.
  useEffect(() => {
    if (isPending || hasRunRef.current) return;
    hasRunRef.current = true;
    for (const n of notifications.filter((n) => !n.isRead)) {
      markRead.mutate(n.id);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isPending, notifications]);

  return (
    <Panel className={styles.panel}>
      <h2 className={styles.heading}>Notifications</h2>
      {isPending ? (
        <p className={styles.empty}>Loading…</p>
      ) : notifications.length === 0 ? (
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
