import { Alert, Button, TextField } from '@marketpulse/ui';
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
    // The disabled attribute on the submit button is UI; this is the invariant it stands
    // for — a second submit event (e.g. a stray Enter) must not fire a second mutation
    // while one is already in flight.
    if (record.isPending) return;

    const unitsValue = Number(units);
    const priceValue = Number(price);
    if (
      ticker.trim() === '' ||
      !Number.isFinite(unitsValue) ||
      unitsValue <= 0 ||
      !Number.isFinite(priceValue) ||
      priceValue <= 0
    ) {
      return;
    }

    // A new submission is new intent: fresh key, whatever happened before.
    keyRef.current = crypto.randomUUID();
    submit(keyRef.current);
  }

  function handleRetry() {
    // Same intent, same key — the server replays or reports in-flight, never duplicates.
    if (keyRef.current) submit(keyRef.current);
  }

  // Retry with the same key whenever the outcome is unknown or explicitly retryable: a 409
  // (concurrent update, or the key already in flight) is the server-known ambiguous case;
  // an error with no HTTP status is a network failure — the client never learned whether the
  // request landed, which is exactly the ambiguous outcome the idempotency key exists for.
  // A definitive 4xx/5xx-with-status is not retryable — resending would just repeat it.
  const retryable =
    record.isError && (record.error.status === 409 || record.error.status === undefined);

  return (
    <section aria-labelledby="trade-heading">
      <h3 id="trade-heading" className={styles.heading}>
        Record a trade
      </h3>
      <form className={styles.form} onSubmit={handleSubmit}>
        <div className={styles.textField}>
          <TextField
            id="trade-ticker"
            label="Ticker"
            value={ticker}
            onChange={(e) => setTicker(e.target.value)}
            maxLength={8}
            required
          />
        </div>
        {/* No Select primitive exists yet in packages/ui, so this is a plain label + select
            given the same visible-label treatment as TextField rather than an aria-label —
            consistency of visible labels is the point, not which element supplies it. */}
        <div className={styles.selectField}>
          <label className={styles.selectLabel} htmlFor="trade-side">
            Side
          </label>
          <select
            id="trade-side"
            className={styles.select}
            value={side}
            onChange={(e) => setSide(e.target.value as 'Buy' | 'Sell')}
          >
            <option value="Buy">Buy</option>
            <option value="Sell">Sell</option>
          </select>
        </div>
        <div className={styles.textField}>
          <TextField
            id="trade-units"
            label="Units"
            type="number"
            step="0.000001"
            min="0.000001"
            value={units}
            onChange={(e) => setUnits(e.target.value)}
            required
          />
        </div>
        <div className={styles.textField}>
          <TextField
            id="trade-price"
            label="Price"
            type="number"
            step="0.01"
            min="0.01"
            value={price}
            onChange={(e) => setPrice(e.target.value)}
            required
          />
        </div>
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
