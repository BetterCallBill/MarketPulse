import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Button } from './Button';

describe('Button', () => {
  it('renders its label as an accessible name', () => {
    render(<Button>Add</Button>);

    expect(screen.getByRole('button', { name: 'Add' })).toBeInTheDocument();
  });

  it('defaults to the primary variant', () => {
    render(<Button>Add</Button>);

    expect(screen.getByRole('button')).toHaveAttribute('data-variant', 'primary');
  });

  it('carries the requested variant as a data attribute', () => {
    render(<Button variant="danger">Remove</Button>);

    expect(screen.getByRole('button')).toHaveAttribute('data-variant', 'danger');
  });

  it('forwards disabled state', () => {
    render(<Button disabled>Add</Button>);

    expect(screen.getByRole('button')).toBeDisabled();
  });

  it('defaults to type button so it cannot submit a form by accident', () => {
    render(<Button>Add</Button>);

    expect(screen.getByRole('button')).toHaveAttribute('type', 'button');
  });
});
