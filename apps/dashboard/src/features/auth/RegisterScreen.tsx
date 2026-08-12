import { Alert, Button, Panel, TextField } from '@marketpulse/ui';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import styles from './AuthScreen.module.css';
import { useRegister } from './useSession';

const MINIMUM_PASSWORD_LENGTH = 12;

export function RegisterScreen() {
  const register = useRegister();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  const tooShort = password.length > 0 && password.length < MINIMUM_PASSWORD_LENGTH;

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (tooShort) return;
    register.mutate({ email, password }, { onSuccess: () => navigate('/', { replace: true }) });
  }

  return (
    <section className={styles.screen} aria-labelledby="register-heading">
      <Panel>
        <h2 className={styles.heading} id="register-heading">
          Create an account
        </h2>

        <form className={styles.form} onSubmit={handleSubmit}>
          <TextField
            id="register-email"
            label="Email"
            type="email"
            autoComplete="username"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
          />
          <TextField
            id="register-password"
            label="Password"
            type="password"
            autoComplete="new-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            hint={`At least ${MINIMUM_PASSWORD_LENGTH} characters. A memorable phrase beats a short, complicated password.`}
            required
          />
          <Button type="submit" disabled={register.isPending || tooShort}>
            Create account
          </Button>
        </form>

        {register.isError && (
          <div className={styles.error}>
            <Alert>{register.error.message}</Alert>
          </div>
        )}

        <p className={styles.footer}>
          Already registered? <Link to="/login">Sign in</Link>
        </p>
      </Panel>
    </section>
  );
}
