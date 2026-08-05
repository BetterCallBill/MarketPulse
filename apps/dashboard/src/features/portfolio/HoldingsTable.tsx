import { Panel } from '@marketpulse/ui';
import { PriceCell } from '../prices/PriceCell';
import { isStale } from '../prices/streamReducer';
import { useNow } from '../prices/useNow';
import { usePriceStream } from '../prices/usePriceStream';
import styles from './HoldingsTable.module.css';
import { usePortfolio } from './usePortfolio';

/** Signed money: the sign is the signal, colour only reinforces it. */
function signedMoney(value: number): string {
  const sign = value < 0 ? '−' : '+';
  return `${sign}$${Math.abs(value).toFixed(2)}`;
}

export function HoldingsTable() {
  const { data, isPending, isError } = usePortfolio();
  const stream = usePriceStream();
  const now = useNow();

  if (isPending) return <p>Loading portfolio…</p>;
  if (isError) return <p role="alert">Could not load your portfolio.</p>;

  const holdings = data.holdings;

  // ADR-007's third worked example: server truth × live stream, joined at render.
  const rows = holdings.map((h) => {
    const live = stream.prices[h.ticker]?.price;
    return {
      ...h,
      live,
      unrealised: live === undefined ? undefined : h.units * (live - h.averageCost),
    };
  });

  // A partial sum lies — the total is only a number when every held ticker has ticked.
  const totalUnrealised = rows.every((r) => r.unrealised !== undefined)
    ? rows.reduce((sum, r) => sum + (r.unrealised ?? 0), 0)
    : undefined;

  return (
    <section aria-labelledby="holdings-heading">
      <h3 id="holdings-heading" className={styles.heading}>
        Holdings
      </h3>
      <Panel className={styles.tableWrap}>
        {holdings.length === 0 ? (
          <p className={styles.empty}>No holdings yet. Record your first trade below.</p>
        ) : (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">Ticker</th>
                <th scope="col" className={styles.numeric}>
                  Units
                </th>
                <th scope="col" className={styles.numeric}>
                  Avg cost
                </th>
                <th scope="col" className={styles.numeric}>
                  Last
                </th>
                <th scope="col" className={styles.numeric}>
                  Realised P&L
                </th>
                <th scope="col" className={styles.numeric}>
                  Unrealised P&L
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.ticker}>
                  <td className={styles.ticker}>{row.ticker}</td>
                  <td className={styles.numeric}>{row.units}</td>
                  <td className={styles.numeric}>${row.averageCost.toFixed(2)}</td>
                  <td className={styles.numeric}>
                    <PriceCell
                      ticker={row.ticker}
                      price={row.live}
                      stale={isStale(stream, row.ticker, now)}
                      disconnected={stream.status === 'reconnecting'}
                      direction={stream.prices[row.ticker]?.direction ?? 'neutral'}
                      seq={stream.prices[row.ticker]?.seq ?? 0}
                    />
                  </td>
                  <td className={styles.numeric} data-tone={row.realisedPnL < 0 ? 'down' : 'up'}>
                    {signedMoney(row.realisedPnL)}
                  </td>
                  <td
                    className={styles.numeric}
                    aria-label={`${row.ticker} unrealised P&L`}
                    data-tone={
                      row.unrealised === undefined ? undefined : row.unrealised < 0 ? 'down' : 'up'
                    }
                  >
                    {row.unrealised === undefined ? '—' : signedMoney(row.unrealised)}
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th scope="row" colSpan={4}>
                  Total
                </th>
                <td className={styles.numeric} data-tone={data.totalRealisedPnL < 0 ? 'down' : 'up'}>
                  {signedMoney(data.totalRealisedPnL)}
                </td>
                <td
                  className={styles.numeric}
                  aria-label="Total unrealised P&L"
                  data-tone={
                    totalUnrealised === undefined ? undefined : totalUnrealised < 0 ? 'down' : 'up'
                  }
                >
                  {totalUnrealised === undefined ? '—' : signedMoney(totalUnrealised)}
                </td>
              </tr>
            </tfoot>
          </table>
        )}
      </Panel>
    </section>
  );
}
