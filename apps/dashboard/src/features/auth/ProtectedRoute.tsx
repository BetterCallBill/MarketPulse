import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { useSession } from './useSession';

export function ProtectedRoute({ children }: { children: ReactNode }) {
  const { isPending, isError } = useSession();

  if (isPending) return <p>Checking your session…</p>;
  if (isError) return <Navigate to="/login" replace />;

  return <>{children}</>;
}
