import { ApiError, type CandleInterval } from '@marketpulse/api-client';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../../api';

/** Windows sized against 7a's 1000-bucket cap and its 7-day retention. */
const WINDOW_MS: Record<CandleInterval, number> = {
  '1m': 6 * 60 * 60 * 1000, // 360 buckets
  '5m': 24 * 60 * 60 * 1000, // 288
  '1h': 7 * 24 * 60 * 60 * 1000, // 168
  '1d': 7 * 24 * 60 * 60 * 1000, // ≤7 — thin until retention grows, and the UI says so
};

const FRESHNESS: Record<CandleInterval, { staleTime: number; refetchInterval: number | false }> = {
  '1m': { staleTime: 30_000, refetchInterval: 60_000 },
  '5m': { staleTime: 30_000, refetchInterval: 60_000 },
  '1h': { staleTime: 300_000, refetchInterval: false },
  '1d': { staleTime: 300_000, refetchInterval: false },
};

export function useCandles(ticker: string, interval: CandleInterval) {
  return useQuery({
    queryKey: ['candles', ticker, interval] as const,
    queryFn: ({ signal }) => {
      const to = new Date();
      const from = new Date(to.getTime() - WINDOW_MS[interval]);
      return apiClient.getCandles(ticker, interval, from.toISOString(), to.toISOString(), signal);
    },
    staleTime: FRESHNESS[interval].staleTime,
    refetchInterval: FRESHNESS[interval].refetchInterval,
    // An unknown ticker is not transient; everything else gets the default treatment.
    retry: (failureCount, error) =>
      !(error instanceof ApiError && error.status === 404) && failureCount < 3,
  });
}
