import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../../api';

const sparklinesKey = ['sparklines'] as const;

/**
 * One batch query feeds every row — the endpoint 7a built specifically so this screen
 * never fans out per ticker. The server buckets by minute; polling faster buys nothing.
 */
export function useSparklines() {
  return useQuery({
    queryKey: sparklinesKey,
    queryFn: ({ signal }) => apiClient.getSparklines(signal),
    staleTime: 60_000,
    refetchInterval: 60_000,
  });
}
