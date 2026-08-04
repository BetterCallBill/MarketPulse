import { Alert, Button } from '@marketpulse/ui';
import { useState, type FormEvent } from 'react';
import styles from './AlertCell.module.css';
import { useAlerts, useCreateAlert, useDeleteAlert, useRearmAlert } from './useAlerts';

export interface AlertCellProps {
  ticker: string;
}

/**
 * The rule inline with the price it watches. No rule: a compact create form.
 * A rule: its state (Active/Triggered), re-arm when triggered, and remove.
 */
export function AlertCell({ ticker }: AlertCellProps) {
  const { data: rules } = useAlerts();
  const createAlert = useCreateAlert();
  const deleteAlert = useDeleteAlert();
  const rearmAlert = useRearmAlert();
  const [threshold, setThreshold] = useState('');
  const [direction, setDirection] = useState<'Above' | 'Below'>('Above');

  const rule = rules?.find((r) => r.ticker === ticker);

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const value = Number(threshold);
    if (!Number.isFinite(value) || value <= 0) return;

    createAlert.mutate(
      { ticker, direction, threshold: value },
      { onSuccess: () => setThreshold('') },
    );
  }

  if (rule) {
    return (
      <div className={styles.cell}>
        <span className={styles.status} data-status={rule.status}>
          {rule.status}
        </span>
        <span className={styles.rule}>
          {rule.direction === 'Above' ? '≥' : '≤'} ${rule.threshold.toFixed(2)}
        </span>
        {rule.status === 'Triggered' && (
          <Button
            variant="ghost"
            onClick={() => rearmAlert.mutate(rule.id)}
            aria-label={`Re-arm alert for ${ticker}`}
            disabled={rearmAlert.isPending}
          >
            Re-arm
          </Button>
        )}
        <Button
          variant="ghost"
          onClick={() => deleteAlert.mutate(rule.id)}
          aria-label={`Remove alert for ${ticker}`}
          disabled={deleteAlert.isPending}
        >
          ✕
        </Button>
      </div>
    );
  }

  return (
    <form className={styles.cell} onSubmit={handleSubmit}>
      <select
        className={styles.direction}
        value={direction}
        onChange={(e) => setDirection(e.target.value as 'Above' | 'Below')}
        aria-label={`Alert direction for ${ticker}`}
      >
        <option value="Above">Above</option>
        <option value="Below">Below</option>
      </select>
      <input
        className={styles.threshold}
        type="number"
        step="0.01"
        min="0.01"
        required
        value={threshold}
        onChange={(e) => setThreshold(e.target.value)}
        aria-label={`Alert threshold for ${ticker}`}
      />
      <Button type="submit" disabled={createAlert.isPending} aria-label={`Set alert for ${ticker}`}>
        Set
      </Button>
      {createAlert.isError && <Alert>{createAlert.error.message}</Alert>}
    </form>
  );
}
