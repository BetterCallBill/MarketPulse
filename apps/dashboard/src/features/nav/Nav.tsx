import { NavLink } from 'react-router-dom';
import { useSession } from '../auth/useSession';
import styles from './Nav.module.css';

/** The app's whole navigation story: two screens, one NavLink each. */
export function Nav() {
  const { data: session } = useSession();
  if (!session) return null;

  return (
    <nav aria-label="Primary">
      <NavLink to="/" end className={styles.link}>
        Watchlist
      </NavLink>
      <NavLink to="/portfolio" className={styles.link}>
        Portfolio
      </NavLink>
    </nav>
  );
}
