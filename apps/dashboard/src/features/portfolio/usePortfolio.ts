import {
  ApiError,
  type Portfolio,
  type TradeSide,
  type Transaction,
} from '@marketpulse/api-client';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
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
 * Load-more over the server's skip/take paging. Pages accumulate in the query cache under
 * ['transactions', pageCount]; invalidating ['transactions'] (as a recorded trade does)
 * refetches every held page — and because the new trade is by definition at the head, the
 * simple reset-to-consistency beats reconciling a fresh head against stale deeper pages.
 */
export function useTransactions(pageSize = 20) {
  const [pages, setPages] = useState(1);

  const { data, isPending, isFetching } = useQuery({
    queryKey: [...transactionsKey, pages, pageSize],
    queryFn: ({ signal }) => apiClient.getTransactions(0, pages * pageSize, signal),
    // Keep previous data visible during query-key change (page growth) to avoid blanking
    // the list while the new page loads. Once data arrives, the new full window replaces it.
    placeholderData: keepPreviousData,
  });

  const transactions: Transaction[] = data ?? [];

  return {
    transactions,
    isPending,
    isFetching,
    // A full window means the server may have more; a short one means we drained it.
    hasMore: transactions.length === pages * pageSize,
    loadMore: () => setPages((p) => p + 1),
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
