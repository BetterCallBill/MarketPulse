import { HoldingsTable } from './HoldingsTable';
import styles from './PortfolioScreen.module.css';

export function PortfolioScreen() {
  return (
    <section aria-labelledby="portfolio-heading" className={styles.screen}>
      <h2 id="portfolio-heading" className={styles.heading}>
        Portfolio
      </h2>
      <HoldingsTable />
      {/* TradeForm, History land in Tasks 5–6 */}
    </section>
  );
}
