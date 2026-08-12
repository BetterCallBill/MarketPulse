import { ApiError, type CandleInterval } from '@marketpulse/api-client';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { CandleChart } from './CandleChart';
import styles from './PriceHistoryScreen.module.css';
import { useCandles } from './useCandles';

const INTERVALS: CandleInterval[] = ['1m', '5m', '1h', '1d'];

/** Default export: this module is the React.lazy boundary — see App.tsx. */
export default function PriceHistoryScreen() {
  const params = useParams<{ ticker: string }>();
  const ticker = (params.ticker ?? '').toUpperCase();
  const [interval, setInterval] = useState<CandleInterval>('1m');
  const { data, isPending, isError, error } = useCandles(ticker, interval);

  return (
    <section aria-labelledby="history-heading">
      <div className={styles.header}>
        <h2 className={styles.heading} id="history-heading">
          {ticker} — price history
        </h2>
        <Link to="/">Back to watchlist</Link>
      </div>

      <div className={styles.intervals} role="group" aria-label="Candle interval">
        {INTERVALS.map((candidate) => (
          <button
            key={candidate}
            type="button"
            aria-pressed={candidate === interval}
            onClick={() => setInterval(candidate)}
            className={styles.intervalButton}
          >
            {candidate}
          </button>
        ))}
      </div>

      {isPending && <p role="status">Loading history…</p>}

      {isError && (
        <p role="alert">
          {error instanceof ApiError && error.errorCode === 'unknown-ticker'
            ? `Unknown ticker “${ticker}”.`
            : 'Could not load price history.'}
        </p>
      )}

      {data && data.candles.length === 0 && (
        <p role="status">No history in this range yet — data accrues while the feed runs.</p>
      )}

      {data && data.candles.length > 0 && <CandleChart candles={data.candles} />}
    </section>
  );
}
