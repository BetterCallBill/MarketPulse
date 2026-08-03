import { useNavigate } from 'react-router-dom';
import { useLogout, useSession } from './useSession';

export function SignOutButton() {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();

  if (!session) return null;

  return (
    <div>
      <span>{session.email}</span>
      <button
        type="button"
        onClick={() => logout.mutate(undefined, { onSuccess: () => navigate('/login') })}
      >
        Sign out
      </button>
    </div>
  );
}
