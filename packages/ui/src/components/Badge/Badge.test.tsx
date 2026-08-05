import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Badge } from './Badge';

describe('Badge', () => {
  it('renders the count with an accessible description', () => {
    render(<Badge count={3} label="unread notifications" />);

    expect(screen.getByText('3')).toBeInTheDocument();
    expect(screen.getByText('3 unread notifications')).toBeInTheDocument();
  });

  it('caps the visible count but keeps the real one for screen readers', () => {
    render(<Badge count={12} label="unread notifications" />);

    expect(screen.getByText('9+')).toBeInTheDocument();
    expect(screen.getByText('12 unread notifications')).toBeInTheDocument();
  });

  it('renders nothing at zero', () => {
    const { container } = render(<Badge count={0} label="unread notifications" />);

    expect(container).toBeEmptyDOMElement();
  });
});
