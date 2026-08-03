import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
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
    <section aria-labelledby="register-heading">
      <h2 id="register-heading">Create an account</h2>

      <form onSubmit={handleSubmit}>
        <label htmlFor="register-email">Email</label>
        <input
          id="register-email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />

        <label htmlFor="register-password">Password</label>
        <input
          id="register-password"
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          aria-describedby="password-hint"
          required
        />
        <p id="password-hint">
          At least {MINIMUM_PASSWORD_LENGTH} characters. A memorable phrase beats a short,
          complicated password.
        </p>

        <button type="submit" disabled={register.isPending || tooShort}>
          Create account
        </button>
      </form>

      {register.isError && <p role="alert">{register.error.message}</p>}

      <p>
        Already registered? <Link to="/login">Sign in</Link>
      </p>
    </section>
  );
}
