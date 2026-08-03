import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { TextField } from './TextField';

describe('TextField', () => {
  it('associates its label with its input', () => {
    render(<TextField id="email" label="Email" />);

    expect(screen.getByLabelText('Email')).toBe(screen.getByRole('textbox'));
  });

  it('describes the input with its hint', () => {
    render(<TextField id="password" label="Password" hint="At least 12 characters." />);

    expect(screen.getByLabelText('Password')).toHaveAccessibleDescription(
      'At least 12 characters.',
    );
  });

  it('omits aria-describedby when there is no hint', () => {
    render(<TextField id="email" label="Email" />);

    expect(screen.getByLabelText('Email')).not.toHaveAttribute('aria-describedby');
  });

  it('forwards arbitrary input attributes', () => {
    render(<TextField id="email" label="Email" type="email" autoComplete="username" required />);

    const input = screen.getByLabelText('Email');
    expect(input).toHaveAttribute('type', 'email');
    expect(input).toHaveAttribute('autocomplete', 'username');
    expect(input).toBeRequired();
  });
});
