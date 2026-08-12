import { ApiError, type Notification } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

export const notificationsKey = ['notifications'] as const;

export function useNotifications(enabled: boolean) {
  return useQuery({
    queryKey: notificationsKey,
    queryFn: ({ signal }) => apiClient.getNotifications(signal),
    enabled,
  });
}

export function useMarkRead() {
  const queryClient = useQueryClient();

  return useMutation<void, ApiError, string>({
    mutationFn: (id) => apiClient.markNotificationRead(id),
    // Optimistic: the row is read the moment the user has seen it. The server row is
    // the truth — a failed POST is healed by the invalidation below, not retried.
    onMutate: async (id) => {
      await queryClient.cancelQueries({ queryKey: notificationsKey });
      queryClient.setQueryData<Notification[]>(notificationsKey, (old) =>
        old?.map((n) => (n.id === id ? { ...n, isRead: true } : n)),
      );
    },
    onError: () => queryClient.invalidateQueries({ queryKey: notificationsKey }),
  });
}
