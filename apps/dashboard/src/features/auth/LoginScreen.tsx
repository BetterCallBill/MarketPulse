import { Alert, Button, Panel, TextField } from '@marketpulse/ui';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import styles from './AuthScreen.module.css';
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
    <section className={styles.screen} aria-labelledby="login-heading">
      <Panel>
        <h2 className={styles.heading} id="login-heading">
          Sign in
        </h2>

        <form className={styles.form} onSubmit={handleSubmit}>
          <TextField
            id="login-email"
            label="Email"
            type="email"
            autoComplete="username"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
          />
          <TextField
            id="login-password"
            label="Password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
          <Button type="submit" disabled={login.isPending}>
            Sign in
          </Button>
        </form>

        {login.isError && (
          <div className={styles.error}>
            <Alert>
              {login.error.message}
              {login.error.correlationId && ` (ref: ${login.error.correlationId})`}
            </Alert>
          </div>
        )}

        <p className={styles.footer}>
          No account? <Link to="/register">Create one</Link>
        </p>
      </Panel>
    </section>
  );
}
