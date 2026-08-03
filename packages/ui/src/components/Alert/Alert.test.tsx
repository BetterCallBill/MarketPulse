import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Alert } from './Alert';

describe('Alert', () => {
  it('announces itself with role alert', () => {
    render(<Alert>That password is incorrect.</Alert>);

    expect(screen.getByRole('alert')).toHaveTextContent('That password is incorrect.');
  });

  it('defaults to the danger tone', () => {
    render(<Alert>Something failed.</Alert>);

    expect(screen.getByRole('alert')).toHaveAttribute('data-tone', 'danger');
  });

  it('carries the requested tone', () => {
    render(<Alert tone="info">Reconnecting.</Alert>);

    expect(screen.getByRole('alert')).toHaveAttribute('data-tone', 'info');
  });
});
