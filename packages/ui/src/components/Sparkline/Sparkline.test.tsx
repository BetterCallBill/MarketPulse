import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { Sparkline } from './Sparkline';

describe('Sparkline', () => {
  it('renders a polyline for two or more points', () => {
    const { container } = render(<Sparkline points={[1, 3, 2]} />);
    const polyline = container.querySelector('polyline');
    expect(polyline).not.toBeNull();
    expect(polyline?.getAttribute('points')).toBeTruthy();
  });

  it('renders nothing for fewer than two points', () => {
    const { container } = render(<Sparkline points={[5]} />);
    expect(container.firstChild).toBeNull();
  });

  it('is hidden from assistive technology', () => {
    const { container } = render(<Sparkline points={[1, 2]} />);
    expect(container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('handles a flat series without dividing by zero', () => {
    const { container } = render(<Sparkline points={[7, 7, 7]} />);
    expect(container.querySelector('polyline')?.getAttribute('points')).not.toContain('NaN');
  });
});
