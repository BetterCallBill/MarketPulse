import { Button, Panel } from '@marketpulse/ui';
import styles from './PortfolioScreen.module.css';
import { useTransactions } from './usePortfolio';

export function TransactionHistory({ pageSize = 20 }: { pageSize?: number }) {
  const { transactions, hasMore, loadMore, isPending, isFetching } = useTransactions(pageSize);

  return (
    <section aria-labelledby="history-heading">
      <h3 id="history-heading" className={styles.sectionHeading}>
        History
      </h3>
      <Panel>
        {isPending ? (
          <p className={styles.empty}>Loading history…</p>
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
        {/* During a page fetch, the placeholder list makes hasMore false (0 !== n×pageSize),
            so the button must key off isFetching to stay mounted-and-disabled rather than
            vanishing mid-fetch. */}
        {(hasMore || isFetching) && !isPending && (
          <Button variant="ghost" onClick={loadMore} disabled={isFetching}>
            Load more
          </Button>
        )}
      </Panel>
    </section>
  );
}
