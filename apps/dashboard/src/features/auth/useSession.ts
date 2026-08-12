import { ApiError, type Session } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

export const sessionKey = ['session'] as const;

/**
 * There is no token in the client to store — the session lives entirely in httpOnly
 * cookies the browser manages. "Am I signed in?" is therefore a server question, and
 * this query is the answer.
 */
export function useSession() {
  return useQuery({
    queryKey: sessionKey,
    queryFn: ({ signal }) => apiClient.me(signal),
    // A 401 is the expected answer for a signed-out visitor, not a transient failure.
    retry: false,
  });
}

export function useLogin() {
  const queryClient = useQueryClient();

  return useMutation<Session, ApiError, { email: string; password: string }>({
    mutationFn: ({ email, password }) => apiClient.login(email, password),
    onSuccess: (session) => queryClient.setQueryData(sessionKey, session),
  });
}

export function useRegister() {
  const queryClient = useQueryClient();

  return useMutation<Session, ApiError, { email: string; password: string }>({
    mutationFn: ({ email, password }) => apiClient.register(email, password),
    onSuccess: (session) => queryClient.setQueryData(sessionKey, session),
  });
}

export function useLogout() {
  const queryClient = useQueryClient();

  return useMutation<void, ApiError, void>({
    mutationFn: () => apiClient.logout(),
    // Drop every cached query, not just the session: the watchlist in the cache belongs
    // to the user who just signed out.
    onSuccess: () => queryClient.clear(),
  });
}
