import { ApiError, type Watchlist } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

const watchlistKey = ['watchlist'] as const;

export function useWatchlist() {
  return useQuery({
    queryKey: watchlistKey,
    queryFn: ({ signal }) => apiClient.getWatchlist(signal),
  });
}

export function useAddItem() {
  const queryClient = useQueryClient();

  return useMutation<Watchlist, ApiError, string>({
    mutationFn: (ticker) => apiClient.addItem(ticker),
    onSuccess: (watchlist) => queryClient.setQueryData(watchlistKey, watchlist),
  });
}

export function useRemoveItem() {
  const queryClient = useQueryClient();

  return useMutation<Watchlist, ApiError, string>({
    mutationFn: (ticker) => apiClient.removeItem(ticker),
    onSuccess: (watchlist) => queryClient.setQueryData(watchlistKey, watchlist),
  });
}
