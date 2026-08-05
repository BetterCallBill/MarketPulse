import { Alert, Button } from '@marketpulse/ui';
import { useRef, useState, type FormEvent } from 'react';
import styles from './TradeForm.module.css';
import { useRecordTransaction } from './usePortfolio';

/**
 * The idempotency key is scoped to a submission, not a request and not the form's
 * lifetime: minted when the user submits, reused verbatim by Retry (a resend of the
 * same intent), discarded the moment a new submission starts. This is the client half
 * of the contract the 5a filter implements.
 */
export function TradeForm() {
  const record = useRecordTransaction();
  const [ticker, setTicker] = useState('');
  const [side, setSide] = useState<'Buy' | 'Sell'>('Buy');
  const [units, setUnits] = useState('');
  const [price, setPrice] = useState('');
  const keyRef = useRef<string | null>(null);

  function submit(key: string) {
    record.mutate(
      {
        trade: { ticker: ticker.trim(), side, units: Number(units), price: Number(price) },
        idempotencyKey: key,
      },
      {
        onSuccess: () => {
          keyRef.current = null;
          setTicker('');
          setUnits('');
          setPrice('');
        },
      },
    );
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (ticker.trim() === '' || Number(units) <= 0 || Number(price) <= 0) return;

    // A new submission is new intent: fresh key, whatever happened before.
    keyRef.current = crypto.randomUUID();
    submit(keyRef.current);
  }

  function handleRetry() {
    // Same intent, same key — the server replays or reports in-flight, never duplicates.
    if (keyRef.current) submit(keyRef.current);
  }

  const retryable = record.isError && record.error.status === 409;

  return (
    <section aria-labelledby="trade-heading">
      <h3 id="trade-heading" className={styles.heading}>
        Record a trade
      </h3>
      <form className={styles.form} onSubmit={handleSubmit}>
        <input
          className={styles.field}
          aria-label="Ticker"
          value={ticker}
          onChange={(e) => setTicker(e.target.value)}
          maxLength={8}
          required
        />
        <select
          className={styles.field}
          aria-label="Side"
          value={side}
          onChange={(e) => setSide(e.target.value as 'Buy' | 'Sell')}
        >
          <option value="Buy">Buy</option>
          <option value="Sell">Sell</option>
        </select>
        <input
          className={styles.field}
          aria-label="Units"
          type="number"
          step="0.000001"
          min="0.000001"
          value={units}
          onChange={(e) => setUnits(e.target.value)}
          required
        />
        <input
          className={styles.field}
          aria-label="Price"
          type="number"
          step="0.01"
          min="0.01"
          value={price}
          onChange={(e) => setPrice(e.target.value)}
          required
        />
        <Button type="submit" disabled={record.isPending}>
          Record trade
        </Button>
      </form>

      {record.isError && (
        <div className={styles.error}>
          <Alert>
            {record.error.message}
            {record.error.correlationId && ` (ref: ${record.error.correlationId})`}
          </Alert>
          {retryable && (
            <Button variant="ghost" onClick={handleRetry} disabled={record.isPending}>
              Retry
            </Button>
          )}
        </div>
      )}
    </section>
  );
}
