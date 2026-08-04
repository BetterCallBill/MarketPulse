import { Badge, Button } from '@marketpulse/ui';
import { useState } from 'react';
import { useSession } from '../auth/useSession';
import styles from './Notifications.module.css';
import { NotificationsPanel } from './NotificationsPanel';
import { useNotifications } from './useNotifications';
import { useNotificationStream } from './useNotificationStream';

export function NotificationBell() {
  const { data: session } = useSession();
  const [open, setOpen] = useState(false);
  const signedIn = Boolean(session);

  const { data: notifications } = useNotifications(signedIn);
  useNotificationStream(signedIn);

  if (!session) return null;

  const unread = notifications?.filter((n) => !n.isRead).length ?? 0;

  return (
    <div className={styles.bell}>
      <Button variant="ghost" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        Notifications <Badge count={unread} label="unread notifications" />
      </Button>
      {open && <NotificationsPanel notifications={notifications ?? []} />}
    </div>
  );
}
