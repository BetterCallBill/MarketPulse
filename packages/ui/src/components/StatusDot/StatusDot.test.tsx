import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { StatusDot } from './StatusDot';

describe('StatusDot', () => {
  it('labels a live connection in text, not colour alone', () => {
    render(<StatusDot status="connected" />);

    expect(screen.getByText('Live')).toBeInTheDocument();
  });

  it('labels a dropped connection in text', () => {
    render(<StatusDot status="reconnecting" />);

    expect(screen.getByText('Reconnecting…')).toBeInTheDocument();
  });

  it('carries the status as a data attribute for styling', () => {
    const { container } = render(<StatusDot status="connecting" />);

    expect(container.querySelector('[data-status="connecting"]')).not.toBeNull();
  });

  it('hides the decorative dot from assistive technology', () => {
    const { container } = render(<StatusDot status="connected" />);

    expect(container.querySelector('[aria-hidden="true"]')).not.toBeNull();
  });
});
