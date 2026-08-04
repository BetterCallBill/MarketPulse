import { notificationPushSchema, type Notification } from '@marketpulse/api-client';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { API_BASE_URL } from '../../api';
import { alertsKey } from '../alerts/useAlerts';
import { infiniteReconnectPolicy } from '../realtime/reconnectPolicy';
import { notificationsKey } from './useNotifications';

/**
 * Live notifications patched straight into the TanStack Query cache. The push is the
 * optimisation and the server row is the truth: a reconnect invalidates the query, so
 * anything missed while disconnected converges on the next refetch. This is the whole
 * client-state story — see ADR-007.
 */
export function useNotificationStream(enabled: boolean) {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!enabled) return;

    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/notifications`)
      .withAutomaticReconnect(infiniteReconnectPolicy)
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('notification', (payload: unknown) => {
      const parsed = notificationPushSchema.safeParse(payload);
      if (!parsed.success) return;

      const notification: Notification = { ...parsed.data, isRead: false };

      queryClient.setQueryData<Notification[]>(notificationsKey, (old) =>
        old
          ? [notification, ...old.filter((n) => n.id !== notification.id)]
          : [notification],
      );

      // A notification means some rule just flipped to Triggered.
      void queryClient.invalidateQueries({ queryKey: alertsKey });
    });

    // Whatever was pushed while disconnected is already a row — refetch converges.
    connection.onreconnected(() => {
      void queryClient.invalidateQueries({ queryKey: notificationsKey });
    });

    connection.start().catch(() => {
      // withAutomaticReconnect owns retries; a failed initial start is retried by the
      // next mount. Notifications have no dedicated "reconnecting" UI — the price
      // stream's indicator already reports realtime health.
    });

    return () => {
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop();
      }
    };
  }, [enabled, queryClient]);
}
