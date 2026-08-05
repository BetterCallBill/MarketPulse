import { ApiError, type AlertDirection, type AlertRule } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

export const alertsKey = ['alerts'] as const;

export function useAlerts() {
  return useQuery({
    queryKey: alertsKey,
    queryFn: ({ signal }) => apiClient.getAlerts(signal),
  });
}

export function useCreateAlert() {
  const queryClient = useQueryClient();

  return useMutation<
    AlertRule,
    ApiError,
    { ticker: string; direction: AlertDirection; threshold: number }
  >({
    mutationFn: ({ ticker, direction, threshold }) =>
      apiClient.createAlert(ticker, direction, threshold),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: alertsKey }),
  });
}

export function useDeleteAlert() {
  const queryClient = useQueryClient();

  return useMutation<void, ApiError, string>({
    mutationFn: (id) => apiClient.deleteAlert(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: alertsKey }),
  });
}

export function useRearmAlert() {
  const queryClient = useQueryClient();

  return useMutation<AlertRule, ApiError, string>({
    mutationFn: (id) => apiClient.rearmAlert(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: alertsKey }),
  });
}
