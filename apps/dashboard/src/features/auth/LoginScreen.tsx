import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useLogin } from './useSession';

export function LoginScreen() {
  const login = useLogin();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    login.mutate({ email, password }, { onSuccess: () => navigate('/', { replace: true }) });
  }

  return (
    <section aria-labelledby="login-heading">
      <h2 id="login-heading">Sign in</h2>

      <form onSubmit={handleSubmit}>
        <label htmlFor="login-email">Email</label>
        <input
          id="login-email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />

        <label htmlFor="login-password">Password</label>
        <input
          id="login-password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />

        <button type="submit" disabled={login.isPending}>
          Sign in
        </button>
      </form>

      {login.isError && (
        <p role="alert">
          {login.error.message}
          {login.error.correlationId && ` (ref: ${login.error.correlationId})`}
        </p>
      )}

      <p>
        No account? <Link to="/register">Create one</Link>
      </p>
    </section>
  );
}
