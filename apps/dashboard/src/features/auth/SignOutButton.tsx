import { Button } from '@marketpulse/ui';
import { useNavigate } from 'react-router-dom';
import styles from '../../app.module.css';
import { useLogout, useSession } from './useSession';

export function SignOutButton() {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();

  if (!session) return null;

  return (
    <div className={styles.session}>
      <span className={styles.email}>{session.email}</span>
      <Button
        variant="ghost"
        onClick={() => logout.mutate(undefined, { onSuccess: () => navigate('/login') })}
      >
        Sign out
      </Button>
    </div>
  );
}
