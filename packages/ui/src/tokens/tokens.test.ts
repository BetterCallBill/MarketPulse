import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// Assigned to a variable (rather than inlined) so Vite's static `new URL(x, import.meta.url)`
// asset-URL analysis doesn't intercept it under the jsdom test environment and rewrite it to a
// http://localhost:3000/... dev-server URL instead of the real file:// URL.
const tokensUrl = import.meta.url;
const css = readFileSync(fileURLToPath(new URL('./tokens.css', tokensUrl)), 'utf8');

function declaredTokens(source: string): Map<string, string> {
  const tokens = new Map<string, string>();
  for (const match of source.matchAll(/(--mp-[a-z0-9-]+)\s*:\s*([^;]+);/g)) {
    tokens.set(match[1]!, match[2]!.trim());
  }
  return tokens;
}

const TOKENS = declaredTokens(css);

/** Follows a semantic token through its var() chain down to a literal value. */
function resolve(name: string, seen = new Set<string>()): string {
  if (seen.has(name)) throw new Error(`Circular token reference at ${name}`);
  seen.add(name);

  const value = TOKENS.get(name);
  if (value === undefined) throw new Error(`Token ${name} is not declared`);

  const reference = /^var\((--mp-[a-z0-9-]+)\)$/.exec(value);
  return reference === null ? value : resolve(reference[1]!, seen);
}

function channel(component: number): number {
  const c = component / 255;
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function luminance(hex: string): number {
  const value = hex.trim().replace('#', '');
  if (!/^[0-9a-f]{6}$/i.test(value)) throw new Error(`Not a 6-digit hex colour: ${hex}`);

  return (
    0.2126 * channel(parseInt(value.slice(0, 2), 16)) +
    0.7152 * channel(parseInt(value.slice(2, 4), 16)) +
    0.0722 * channel(parseInt(value.slice(4, 6), 16))
  );
}

function contrast(foreground: string, background: string): number {
  const a = luminance(resolve(foreground));
  const b = luminance(resolve(background));
  const [lighter, darker] = a > b ? [a, b] : [b, a];

  return (lighter + 0.05) / (darker + 0.05);
}

const TEXT_PAIRS: Array<[string, string]> = [
  ['--mp-text-primary', '--mp-surface-base'],
  ['--mp-text-primary', '--mp-surface-raised'],
  ['--mp-text-secondary', '--mp-surface-base'],
  ['--mp-text-secondary', '--mp-surface-raised'],
  ['--mp-text-muted', '--mp-surface-base'],
  ['--mp-text-muted', '--mp-surface-raised'],
  ['--mp-price-up', '--mp-surface-base'],
  ['--mp-price-up', '--mp-surface-raised'],
  ['--mp-price-down', '--mp-surface-base'],
  ['--mp-price-down', '--mp-surface-raised'],
  ['--mp-accent', '--mp-surface-base'],
  ['--mp-accent', '--mp-surface-raised'],
  ['--mp-danger', '--mp-surface-base'],
  ['--mp-danger', '--mp-surface-raised'],
  ['--mp-text-primary', '--mp-surface-hover'],
  ['--mp-price-up', '--mp-surface-hover'],
  ['--mp-price-down', '--mp-surface-hover'],
  ['--mp-danger', '--mp-surface-hover'],
];

describe('design tokens', () => {
  it.each(TEXT_PAIRS)('%s on %s meets WCAG AA for body text (4.5:1)', (fg, bg) => {
    expect(contrast(fg, bg)).toBeGreaterThanOrEqual(4.5);
  });

  it('the focus ring meets the 3:1 minimum for UI boundaries on both surfaces', () => {
    expect(contrast('--mp-focus-ring', '--mp-surface-base')).toBeGreaterThanOrEqual(3);
    expect(contrast('--mp-focus-ring', '--mp-surface-raised')).toBeGreaterThanOrEqual(3);
  });

  it('every semantic token resolves to a literal value', () => {
    const semantic = [...TOKENS.keys()].filter((name) => TOKENS.get(name)!.startsWith('var('));

    expect(semantic.length).toBeGreaterThan(0);
    for (const name of semantic) {
      expect(() => resolve(name)).not.toThrow();
    }
  });

  it('declares a stale opacity between 0 and 1', () => {
    expect(Number(resolve('--mp-opacity-stale'))).toBeGreaterThan(0);
    expect(Number(resolve('--mp-opacity-stale'))).toBeLessThan(1);
  });
});
