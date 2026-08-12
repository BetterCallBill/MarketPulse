import { Button, Panel } from '@marketpulse/ui';
import styles from './PortfolioScreen.module.css';
import { useTransactions } from './usePortfolio';

export function TransactionHistory({ pageSize = 20 }: { pageSize?: number }) {
  const { transactions, hasMore, loadMore, isPending, isFetching, isError } =
    useTransactions(pageSize);

  return (
    <section aria-labelledby="history-heading">
      <h3 id="history-heading" className={styles.sectionHeading}>
        History
      </h3>
      <Panel>
        {isPending ? (
          <p className={styles.empty}>Loading history…</p>
        ) : isError ? (
          <p role="alert">Could not load your history.</p>
        ) : transactions.length === 0 ? (
          <p className={styles.empty}>No trades recorded yet.</p>
        ) : (
          <ul className={styles.historyList}>
            {transactions.map((t) => (
              <li key={t.id} className={styles.historyItem}>
                <span>{`${t.side} ${t.units} ${t.ticker} @ $${t.price.toFixed(2)}`}</span>
                <time dateTime={t.occurredUtc}>
                  {new Date(t.occurredUtc).toLocaleString()}
                </time>
              </li>
            ))}
          </ul>
        )}
        {/* useInfiniteQuery keeps already-fetched pages visible while the next page is in
            flight, so rows never blank mid-fetch; the button just needs to stay mounted and
            disabled (via isFetchingNextPage) instead of vanishing while hasMore momentarily
            lags the request. */}
        {(hasMore || isFetching) && !isPending && (
          <Button variant="ghost" onClick={() => loadMore()} disabled={isFetching}>
            Load more
          </Button>
        )}
      </Panel>
    </section>
  );
}
