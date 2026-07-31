import { useState, type FormEvent } from 'react';
import { PriceCell } from '../prices/PriceCell';
import { isStale } from '../prices/streamReducer';
import { usePriceStream } from '../prices/usePriceStream';
import { useAddItem, useRemoveItem, useWatchlist } from './useWatchlist';

export function WatchlistScreen() {
  const { data, isPending, isError } = useWatchlist();
  const addItem = useAddItem();
  const removeItem = useRemoveItem();
  const [ticker, setTicker] = useState('');
  const stream = usePriceStream();
  const now = Date.now();

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
      <h2 id="watchlist-heading">Watchlist</h2>

      {stream.status === 'reconnecting' && (
        <p role="status">Reconnecting to the price feed…</p>
      )}

      <form onSubmit={handleSubmit}>
        <label htmlFor="add-ticker">Add ticker</label>
        <input
          id="add-ticker"
          value={ticker}
          onChange={(e) => setTicker(e.target.value)}
          maxLength={8}
        />
        <button type="submit" disabled={addItem.isPending}>
          Add
        </button>
      </form>

      {addItem.isError && (
        <p role="alert">
          {addItem.error.message}
          {addItem.error.correlationId && ` (ref: ${addItem.error.correlationId})`}
        </p>
      )}

      <ul>
        {data.items.map((item) => (
          <li key={item.ticker}>
            <span>{item.ticker}</span>
            <PriceCell
              ticker={item.ticker}
              price={stream.prices[item.ticker]?.price}
              stale={isStale(stream, item.ticker, now)}
              disconnected={stream.status === 'reconnecting'}
            />
            <button
              type="button"
              onClick={() => removeItem.mutate(item.ticker)}
              aria-label={`Remove ${item.ticker}`}
            >
              Remove
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
