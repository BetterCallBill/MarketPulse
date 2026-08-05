import { ApiError, type Portfolio, type TradeSide } from '@marketpulse/api-client';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

export const portfolioKey = ['portfolio'] as const;
export const transactionsKey = ['transactions'] as const;

export function usePortfolio() {
  return useQuery({
    queryKey: portfolioKey,
    queryFn: ({ signal }) => apiClient.getPortfolio(signal),
  });
}

/**
 * Load-more over the server's skip/take paging, via useInfiniteQuery's skip-page
 * accumulation: every request's `take` is fixed at `pageSize`, never near the server's
 * cap (`GetTransactionsQuery` rejects `Take > 100`). The rejected alternative — a single
 * growing-window query re-requesting `take = pages * pageSize` from `skip = 0` — hits that
 * cap at the sixth default-sized load-more (`take=120`), fails with a 400, and (because
 * `keepPreviousData` kept the old rows and a failed fetch still reports a non-full window)
 * looks exactly like "history exhausted" instead of "request rejected". Each page here is
 * its own request at `skip = itemsFetchedSoFar`, so the window never grows past `pageSize`.
 * Pages accumulate in the query cache under ['transactions', pageSize]; invalidating
 * ['transactions'] (as a recorded trade does) refetches every held page — and because the
 * new trade is by definition at the head, the simple reset-to-consistency beats reconciling
 * a fresh head against stale deeper pages.
 */
export function useTransactions(pageSize = 20) {
  const { data, isPending, isFetchingNextPage, hasNextPage, fetchNextPage, isError } =
    useInfiniteQuery({
      queryKey: [...transactionsKey, pageSize],
      queryFn: ({ pageParam, signal }) => apiClient.getTransactions(pageParam, pageSize, signal),
      initialPageParam: 0,
      // A full page means the server may have more; a short one means we drained it —
      // same heuristic as before, now applied per page instead of to a growing window.
      getNextPageParam: (lastPage, allPages) =>
        lastPage.length === pageSize ? allPages.flat().length : undefined,
    });

  return {
    transactions: data?.pages.flat() ?? [],
    isPending,
    isFetching: isFetchingNextPage,
    hasMore: hasNextPage,
    loadMore: fetchNextPage,
    isError,
  };
}

export function useRecordTransaction() {
  const queryClient = useQueryClient();

  return useMutation<
    Portfolio,
    ApiError,
    {
      trade: { ticker: string; side: TradeSide; units: number; price: number };
      idempotencyKey: string;
    }
  >({
    mutationFn: ({ trade, idempotencyKey }) =>
      apiClient.recordTransaction(trade, idempotencyKey),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: portfolioKey });
      void queryClient.invalidateQueries({ queryKey: transactionsKey });
    },
  });
}
