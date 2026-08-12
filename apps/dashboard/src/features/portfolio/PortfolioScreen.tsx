import { HoldingsTable } from './HoldingsTable';
import styles from './PortfolioScreen.module.css';
import { TradeForm } from './TradeForm';
import { TransactionHistory } from './TransactionHistory';

export function PortfolioScreen() {
  return (
    <section aria-labelledby="portfolio-heading" className={styles.screen}>
      <h2 id="portfolio-heading" className={styles.heading}>
        Portfolio
      </h2>
      <HoldingsTable />
      <TradeForm />
      <TransactionHistory />
    </section>
  );
}
