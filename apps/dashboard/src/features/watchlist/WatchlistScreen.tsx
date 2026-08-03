import { Alert, Button, Panel, StatusDot, TextField, VisuallyHidden } from '@marketpulse/ui';
import { useState, type FormEvent } from 'react';
import { PriceCell } from '../prices/PriceCell';
import { isStale } from '../prices/streamReducer';
import { useNow } from '../prices/useNow';
import { usePriceStream } from '../prices/usePriceStream';
import styles from './WatchlistScreen.module.css';
import { useAddItem, useRemoveItem, useWatchlist } from './useWatchlist';

export function WatchlistScreen() {
  const { data, isPending, isError } = useWatchlist();
  const addItem = useAddItem();
  const removeItem = useRemoveItem();
  const [ticker, setTicker] = useState('');
  const stream = usePriceStream();
  const now = useNow();

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const value = ticker.trim();
    if (value === '') return;

    addItem.mutate(value, { onSuccess: () => setTicker('') });
  }

  if (isPending) return <p>Loading watchlist…</p>;
  if (isError) return <p role="alert">Could not load your watchlist.</p>;

  return (
    <section aria-labelledby="watchlist-heading">
      <div className={styles.header}>
        <h2 className={styles.heading} id="watchlist-heading">
          Watchlist
        </h2>
        <StatusDot status={stream.status} />
      </div>

      {stream.status === 'reconnecting' && (
        <p role="status" className={styles.notice}>
          Reconnecting to the price feed…
        </p>
      )}

      <form className={styles.addForm} onSubmit={handleSubmit}>
        <TextField
          id="add-ticker"
          label="Add ticker"
          value={ticker}
          onChange={(e) => setTicker(e.target.value)}
          maxLength={8}
        />
        <Button type="submit" disabled={addItem.isPending}>
          Add
        </Button>
      </form>

      {addItem.isError && (
        <Alert>
          {addItem.error.message}
          {addItem.error.correlationId && ` (ref: ${addItem.error.correlationId})`}
        </Alert>
      )}

      <Panel className={styles.tableWrap}>
        {data.items.length === 0 ? (
          <p className={styles.empty}>No tickers yet. Add one above to start streaming prices.</p>
        ) : (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">Ticker</th>
                <th scope="col" className={styles.numeric}>
                  Last
                </th>
                <th scope="col" className={styles.actions}>
                  <VisuallyHidden>Actions</VisuallyHidden>
                </th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((item) => {
                const entry = stream.prices[item.ticker];

                return (
                  <tr key={item.ticker}>
                    <td className={styles.ticker}>{item.ticker}</td>
                    <td className={styles.numeric}>
                      <PriceCell
                        ticker={item.ticker}
                        price={entry?.price}
                        stale={isStale(stream, item.ticker, now)}
                        disconnected={stream.status === 'reconnecting'}
                        direction={entry?.direction ?? 'neutral'}
                        seq={entry?.seq ?? 0}
                      />
                    </td>
                    <td className={styles.actions}>
                      <Button
                        variant="danger"
                        onClick={() => removeItem.mutate(item.ticker)}
                        aria-label={`Remove ${item.ticker}`}
                      >
                        Remove
                      </Button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </Panel>
    </section>
  );
}
