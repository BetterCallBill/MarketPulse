# Slice 3 — Dashboard Design System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the dashboard a dark, dense, trading-terminal appearance built on a two-layer design-token system in a new `packages/ui`, without changing a single API contract.

**Architecture:** `packages/ui` ships raw TypeScript source and CSS (no build step, exactly like `packages/api-client`). `tokens.css` declares primitive tokens (raw values) and semantic tokens (meaning). Components consume **only** semantic tokens through colocated CSS Modules. Component state that tests assert on lives on `data-` attributes, not class names, so assertions do not depend on a style engine jsdom does not run.

**Tech Stack:** React 18.3, TypeScript 5.6 (strict, `noUncheckedIndexedAccess`), Vite 5, CSS Modules, Vitest 2, Testing Library, Playwright.

**Spec:** [2026-08-03-dashboard-design-system-design.md](../specs/2026-08-03-dashboard-design-system-design.md)

## Global Constraints

Every task's requirements implicitly include this section.

1. **No `Co-Authored-By` trailer on any commit.** Carried over from slices 1 and 2.
2. **Merge target is `test`, never `main`.** `main` advances only after a human-gated verification run.
3. **Components may reference only semantic tokens.** Never a primitive token (`--mp-grey-*`, `--mp-green-*`, `--mp-red-*`, `--mp-blue-*`), never a hex literal, never a raw `px` colour. Task 3 adds an automated guard; do not wait for it to be your conscience.
4. **These strings are asserted by the Playwright suite and must not change:** headings `Sign in` and `Watchlist`; button names `Sign in`, `Create account`, `Add` (exact), `Sign out`, `Remove {TICKER}`; labels `Email`, `Password`, `Add ticker`, `{TICKER} price`.
5. **The element labelled `{TICKER} price` must contain only the formatted price** (`$62.40`) — E2E asserts `/^\$\d/` against its text. No arrow, no glyph, no whitespace-separated affix inside it.
6. **Preserve existing semantics:** `role="alert"` on errors, `role="status"` on the reconnection notice, landmark structure (`header`/`main`), `aria-labelledby` on sections, and every existing `htmlFor`/`id` label association.
7. **No new runtime dependencies.** `packages/ui` has no `dependencies` beyond React as a peer. Dev-only additions (vitest, jsdom, testing-library) are fine.
8. **No inline `style` attributes** anywhere in `apps/dashboard` or `packages/ui` when the slice is done.
9. Package manager is **pnpm**. Run commands from the repository root.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `packages/ui/package.json` | Workspace package manifest; `main`/`types` → `./src/index.ts`, no build |
| `packages/ui/tsconfig.json` | Mirrors `packages/api-client/tsconfig.json` plus DOM libs and JSX |
| `packages/ui/vitest.config.ts` | jsdom environment, RTL setup file |
| `packages/ui/src/test/setup.ts` | `@testing-library/jest-dom/vitest` import |
| `packages/ui/src/index.ts` | Barrel export for every primitive |
| `packages/ui/src/tokens/tokens.css` | The design system: primitive layer, then semantic layer |
| `packages/ui/src/tokens/tokens.test.ts` | Parses the CSS and proves contrast ratios and token resolution |
| `packages/ui/src/tokens/noPrimitiveLeak.test.ts` | Guard: no component CSS references a primitive or hex literal |
| `packages/ui/src/components/Button/*` | Button primitive + CSS Module |
| `packages/ui/src/components/TextField/*` | Label + input + hint/error wiring |
| `packages/ui/src/components/Alert/*` | `role="alert"` container, `danger`/`info` tones |
| `packages/ui/src/components/Panel/*` | Raised surface container |
| `packages/ui/src/components/StatusDot/*` | Connection indicator, dot + text label |
| `packages/ui/src/components/VisuallyHidden/*` | Screen-reader-only text |
| `apps/dashboard/src/app.module.css` | Shell layout: header, main, page frame |
| `apps/dashboard/src/features/auth/AuthScreen.module.css` | Shared login/register card layout |
| `apps/dashboard/src/features/watchlist/WatchlistScreen.module.css` | Table, empty state, add form |
| `apps/dashboard/src/features/prices/PriceCell.module.css` | Numerals, direction colours, flash keyframes |

**Modified:**

| File | Change |
|---|---|
| `apps/dashboard/package.json` | Add `@marketpulse/ui: workspace:*` |
| `apps/dashboard/index.html` | `color-scheme: dark` meta |
| `apps/dashboard/src/main.tsx` | Import `tokens.css` once |
| `apps/dashboard/src/App.tsx` | Shell layout, status indicator |
| `apps/dashboard/src/features/prices/streamReducer.ts` | `direction` + `seq` per ticker |
| `apps/dashboard/src/features/prices/streamReducer.test.ts` | Exact-shape assertion updated; direction cases added |
| `apps/dashboard/src/features/prices/PriceCell.tsx` | Arrow sibling, data attributes, no inline style |
| `apps/dashboard/src/features/prices/PriceCell.test.tsx` | `toHaveStyle` → `data-stale` assertions |
| `apps/dashboard/src/features/auth/LoginScreen.tsx` | Rebuilt on primitives |
| `apps/dashboard/src/features/auth/RegisterScreen.tsx` | Rebuilt on primitives |
| `apps/dashboard/src/features/auth/SignOutButton.tsx` | Rebuilt on primitives |
| `apps/dashboard/src/features/watchlist/WatchlistScreen.tsx` | `<ul>` → `<table>`, empty state |
| `tests/e2e/specs/authentication.spec.ts` | Vacuous listitem assertion → row count |
| `docs/adr/008-design-tokens.md` | New ADR |
| `README.md` | Frontend section matches reality |

---

### Task 1: Create `packages/ui` with the token layer

**Files:**
- Create: `packages/ui/package.json`, `packages/ui/tsconfig.json`, `packages/ui/vitest.config.ts`, `packages/ui/src/test/setup.ts`, `packages/ui/src/index.ts`, `packages/ui/src/tokens/tokens.css`
- Test: `packages/ui/src/tokens/tokens.test.ts`
- Modify: `apps/dashboard/package.json`

**Interfaces:**
- Consumes: nothing.
- Produces: the semantic token names every later task uses — `--mp-surface-base`, `--mp-surface-raised`, `--mp-surface-hover`, `--mp-border-subtle`, `--mp-text-primary`, `--mp-text-secondary`, `--mp-text-muted`, `--mp-accent`, `--mp-price-up`, `--mp-price-down`, `--mp-price-neutral`, `--mp-danger`, `--mp-focus-ring`, `--mp-opacity-stale`, `--mp-space-1`…`--mp-space-8`, `--mp-radius-sm`, `--mp-radius-md`, `--mp-font-ui`, `--mp-font-mono`.

- [ ] **Step 1: Create the package manifest**

`packages/ui/package.json`:

```json
{
  "name": "@marketpulse/ui",
  "version": "0.1.0",
  "type": "module",
  "main": "./src/index.ts",
  "types": "./src/index.ts",
  "exports": {
    ".": "./src/index.ts",
    "./tokens.css": "./src/tokens/tokens.css"
  },
  "scripts": {
    "typecheck": "tsc --noEmit",
    "test": "vitest run",
    "lint": "tsc --noEmit"
  },
  "peerDependencies": {
    "react": "^18.3.1"
  },
  "devDependencies": {
    "@testing-library/jest-dom": "^6.5.0",
    "@testing-library/react": "^16.0.1",
    "@testing-library/user-event": "^14.5.2",
    "@types/react": "^18.3.11",
    "@types/react-dom": "^18.3.1",
    "jsdom": "^25.0.1",
    "react": "^18.3.1",
    "react-dom": "^18.3.1",
    "typescript": "^5.6.0",
    "vitest": "^2.1.0"
  }
}
```

- [ ] **Step 2: Create the TypeScript and Vitest configuration**

`packages/ui/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "skipLibCheck": true,
    "noEmit": true,
    "types": ["vitest/globals", "@testing-library/jest-dom"]
  },
  "include": ["src", "vitest.config.ts"]
}
```

`packages/ui/vitest.config.ts`:

```ts
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
```

`packages/ui/src/test/setup.ts`:

```ts
import '@testing-library/jest-dom/vitest';
```

- [ ] **Step 3: Write the failing token test**

This test reads `tokens.css` off disk with `node:fs` rather than importing it, because Vitest stubs CSS imports by default. It is the mechanism that makes "contrast is verified, not eyeballed" true.

`packages/ui/src/tokens/tokens.test.ts`:

```ts
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const css = readFileSync(fileURLToPath(new URL('./tokens.css', import.meta.url)), 'utf8');

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
  ['--mp-danger', '--mp-surface-base'],
  ['--mp-danger', '--mp-surface-raised'],
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
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `pnpm --filter @marketpulse/ui test`
Expected: FAIL — `ENOENT: no such file or directory ... tokens.css`. If it fails for any other reason, fix that first.

- [ ] **Step 5: Write the token layer**

`packages/ui/src/tokens/tokens.css`:

```css
/*
 * Two layers, and one rule.
 *
 * PRIMITIVES hold raw values and carry no meaning. SEMANTIC tokens reference
 * primitives and carry meaning. Components may use ONLY semantic tokens — that
 * indirection is what makes a second theme a rewrite of this file rather than a
 * hunt through every component. noPrimitiveLeak.test.ts enforces it.
 */
:root {
  /* ---- Primitives: colour ---- */
  --mp-grey-950: #0b0e13;
  --mp-grey-900: #12161d;
  --mp-grey-850: #1a1f28;
  --mp-grey-700: #2a313d;
  --mp-grey-400: #8b95a5;
  --mp-grey-200: #c9d1dc;
  --mp-grey-50: #f2f5f9;
  --mp-green-400: #4ade80;
  --mp-red-400: #f87171;
  --mp-blue-400: #60a5fa;

  /* ---- Primitives: space (4px scale) ---- */
  --mp-space-1: 0.25rem;
  --mp-space-2: 0.5rem;
  --mp-space-3: 0.75rem;
  --mp-space-4: 1rem;
  --mp-space-5: 1.5rem;
  --mp-space-6: 2rem;
  --mp-space-8: 3rem;

  /* ---- Primitives: shape and type ---- */
  --mp-radius-sm: 4px;
  --mp-radius-md: 8px;
  --mp-font-ui: system-ui, -apple-system, 'Segoe UI', sans-serif;
  --mp-font-mono: ui-monospace, SFMono-Regular, 'SF Mono', Menlo, monospace;

  /* ---- Semantic: surfaces and text ---- */
  --mp-surface-base: var(--mp-grey-950);
  --mp-surface-raised: var(--mp-grey-900);
  --mp-surface-hover: var(--mp-grey-850);
  --mp-border-subtle: var(--mp-grey-700);
  --mp-text-primary: var(--mp-grey-50);
  --mp-text-secondary: var(--mp-grey-200);
  --mp-text-muted: var(--mp-grey-400);

  /* ---- Semantic: meaning ---- */
  --mp-accent: var(--mp-blue-400);
  --mp-danger: var(--mp-red-400);
  --mp-focus-ring: var(--mp-blue-400);
  --mp-price-up: var(--mp-green-400);
  --mp-price-down: var(--mp-red-400);
  --mp-price-neutral: var(--mp-text-primary);
  --mp-opacity-stale: 0.45;
}

*,
*::before,
*::after {
  box-sizing: border-box;
}

body {
  margin: 0;
  background: var(--mp-surface-base);
  color: var(--mp-text-primary);
  font-family: var(--mp-font-ui);
  line-height: 1.5;
}

/* Focus is never removed without replacement. */
:focus-visible {
  outline: 2px solid var(--mp-focus-ring);
  outline-offset: 2px;
}
```

- [ ] **Step 6: Create the empty barrel and run the test to verify it passes**

`packages/ui/src/index.ts`:

```ts
export {};
```

Run: `pnpm install && pnpm --filter @marketpulse/ui test`
Expected: PASS — 16 tests (13 pair cases + 3).

- [ ] **Step 7: Wire the package into the dashboard**

In `apps/dashboard/package.json`, add to `dependencies`, keeping alphabetical order (it goes after `@marketpulse/api-client`):

```json
"@marketpulse/ui": "workspace:*",
```

Then in `apps/dashboard/src/main.tsx`, add the token import as the **first** import so tokens precede every module stylesheet:

```tsx
import '@marketpulse/ui/tokens.css';
import { StrictMode } from 'react';
```

- [ ] **Step 8: Verify the workspace still builds**

Run: `pnpm install && pnpm -r typecheck && pnpm --filter @marketpulse/dashboard build`
Expected: all pass, and the built CSS contains the token block.

- [ ] **Step 9: Commit**

```bash
git add packages/ui apps/dashboard/package.json apps/dashboard/src/main.tsx pnpm-lock.yaml
git commit -m "feat: add the packages/ui design token layer

Two layers: primitives hold raw values, semantic tokens hold meaning. A test
parses the CSS and computes WCAG contrast ratios for every text-on-surface pair,
so the palette is verified rather than eyeballed."
```

---

### Task 2: Form primitives — Button and TextField

**Files:**
- Create: `packages/ui/src/components/Button/Button.tsx`, `Button.module.css`, `Button.test.tsx`
- Create: `packages/ui/src/components/TextField/TextField.tsx`, `TextField.module.css`, `TextField.test.tsx`
- Modify: `packages/ui/src/index.ts`

**Interfaces:**
- Consumes: semantic tokens from Task 1.
- Produces:
  - `Button(props: ButtonProps)` where `ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'ghost' | 'danger' }`. Default variant `primary`. Renders `data-variant`.
  - `TextField(props: TextFieldProps)` where `TextFieldProps = InputHTMLAttributes<HTMLInputElement> & { id: string; label: string; hint?: string }`. Renders `<label htmlFor={id}>` + `<input id={id}>`, and when `hint` is present a `<p id={`${id}-hint`}>` wired through `aria-describedby`.

- [ ] **Step 1: Write the failing tests**

`packages/ui/src/components/Button/Button.test.tsx`:

```tsx
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
```

`packages/ui/src/components/TextField/TextField.test.tsx`:

```tsx
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `pnpm --filter @marketpulse/ui test`
Expected: FAIL — cannot resolve `./Button` and `./TextField`.

- [ ] **Step 3: Implement Button**

`packages/ui/src/components/Button/Button.tsx`:

```tsx
import type { ButtonHTMLAttributes } from 'react';
import styles from './Button.module.css';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'ghost' | 'danger';
}

export function Button({ variant = 'primary', type = 'button', ...rest }: ButtonProps) {
  return <button {...rest} type={type} data-variant={variant} className={styles.button} />;
}
```

`packages/ui/src/components/Button/Button.module.css`:

```css
.button {
  display: inline-flex;
  align-items: center;
  gap: var(--mp-space-2);
  padding: var(--mp-space-2) var(--mp-space-4);
  border: 1px solid transparent;
  border-radius: var(--mp-radius-sm);
  font: inherit;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 120ms ease, border-color 120ms ease;
}

.button:disabled {
  opacity: var(--mp-opacity-stale);
  cursor: not-allowed;
}

.button[data-variant='primary'] {
  background: var(--mp-accent);
  color: var(--mp-surface-base);
}

.button[data-variant='primary']:hover:not(:disabled) {
  border-color: var(--mp-text-primary);
}

.button[data-variant='ghost'] {
  background: transparent;
  border-color: var(--mp-border-subtle);
  color: var(--mp-text-secondary);
}

.button[data-variant='ghost']:hover:not(:disabled) {
  background: var(--mp-surface-hover);
  color: var(--mp-text-primary);
}

.button[data-variant='danger'] {
  background: transparent;
  border-color: var(--mp-border-subtle);
  color: var(--mp-danger);
}

.button[data-variant='danger']:hover:not(:disabled) {
  background: var(--mp-surface-hover);
}
```

- [ ] **Step 4: Implement TextField**

`packages/ui/src/components/TextField/TextField.tsx`:

```tsx
import type { InputHTMLAttributes } from 'react';
import styles from './TextField.module.css';

export interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  id: string;
  label: string;
  hint?: string;
}

export function TextField({ id, label, hint, ...rest }: TextFieldProps) {
  const hintId = `${id}-hint`;

  return (
    <div className={styles.field}>
      <label className={styles.label} htmlFor={id}>
        {label}
      </label>
      <input
        {...rest}
        id={id}
        className={styles.input}
        aria-describedby={hint === undefined ? undefined : hintId}
      />
      {hint !== undefined && (
        <p className={styles.hint} id={hintId}>
          {hint}
        </p>
      )}
    </div>
  );
}
```

`packages/ui/src/components/TextField/TextField.module.css`:

```css
.field {
  display: flex;
  flex-direction: column;
  gap: var(--mp-space-1);
}

.label {
  font-size: 0.875rem;
  color: var(--mp-text-secondary);
}

.input {
  padding: var(--mp-space-2) var(--mp-space-3);
  background: var(--mp-surface-base);
  border: 1px solid var(--mp-border-subtle);
  border-radius: var(--mp-radius-sm);
  color: var(--mp-text-primary);
  font: inherit;
}

.input:hover {
  border-color: var(--mp-text-muted);
}

.hint {
  margin: 0;
  font-size: 0.8125rem;
  color: var(--mp-text-muted);
}
```

- [ ] **Step 5: Export both from the barrel**

`packages/ui/src/index.ts`:

```ts
export { Button, type ButtonProps } from './components/Button/Button';
export { TextField, type TextFieldProps } from './components/TextField/TextField';
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `pnpm --filter @marketpulse/ui test && pnpm --filter @marketpulse/ui typecheck`
Expected: PASS — 9 new tests, plus Task 1's 16.

- [ ] **Step 7: Commit**

```bash
git add packages/ui/src
git commit -m "feat: add Button and TextField primitives

State that tests assert on rides on data attributes rather than class names,
because jsdom never applies CSS Module styles."
```

---

### Task 3: Feedback primitives and the token-leak guard

**Files:**
- Create: `packages/ui/src/components/Alert/Alert.tsx`, `Alert.module.css`, `Alert.test.tsx`
- Create: `packages/ui/src/components/Panel/Panel.tsx`, `Panel.module.css`
- Create: `packages/ui/src/components/StatusDot/StatusDot.tsx`, `StatusDot.module.css`, `StatusDot.test.tsx`
- Create: `packages/ui/src/components/VisuallyHidden/VisuallyHidden.tsx`, `VisuallyHidden.module.css`
- Create: `packages/ui/src/tokens/noPrimitiveLeak.test.ts`
- Modify: `packages/ui/src/index.ts`

**Interfaces:**
- Consumes: semantic tokens from Task 1.
- Produces:
  - `Alert({ tone?: 'danger' | 'info', children })` — always `role="alert"`, default tone `danger`, renders `data-tone`.
  - `Panel({ children, className? })` — raised surface `<div>`; `className` appends to the panel class.
  - `StatusDot({ status: 'connecting' | 'connected' | 'reconnecting' })` — renders `data-status`, an `aria-hidden` dot, and a visible text label (`Live`, `Connecting…`, `Reconnecting…`).
  - `VisuallyHidden({ children })` — `<span>` clipped from view but read by screen readers.

- [ ] **Step 1: Write the failing tests**

`packages/ui/src/components/Alert/Alert.test.tsx`:

```tsx
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
```

`packages/ui/src/components/StatusDot/StatusDot.test.tsx`:

```tsx
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `pnpm --filter @marketpulse/ui test`
Expected: FAIL — cannot resolve `./Alert` and `./StatusDot`.

- [ ] **Step 3: Implement the four components**

`packages/ui/src/components/Alert/Alert.tsx`:

```tsx
import type { ReactNode } from 'react';
import styles from './Alert.module.css';

export interface AlertProps {
  tone?: 'danger' | 'info';
  children: ReactNode;
}

export function Alert({ tone = 'danger', children }: AlertProps) {
  return (
    <p role="alert" data-tone={tone} className={styles.alert}>
      {children}
    </p>
  );
}
```

`packages/ui/src/components/Alert/Alert.module.css`:

```css
.alert {
  margin: 0;
  padding: var(--mp-space-3);
  border: 1px solid var(--mp-border-subtle);
  border-left-width: 3px;
  border-radius: var(--mp-radius-sm);
  background: var(--mp-surface-raised);
  font-size: 0.9375rem;
}

.alert[data-tone='danger'] {
  border-left-color: var(--mp-danger);
  color: var(--mp-danger);
}

.alert[data-tone='info'] {
  border-left-color: var(--mp-accent);
  color: var(--mp-text-secondary);
}
```

`packages/ui/src/components/Panel/Panel.tsx`:

```tsx
import type { ReactNode } from 'react';
import styles from './Panel.module.css';

export interface PanelProps {
  children: ReactNode;
  className?: string;
}

export function Panel({ children, className }: PanelProps) {
  return (
    <div className={className === undefined ? styles.panel : `${styles.panel} ${className}`}>
      {children}
    </div>
  );
}
```

`packages/ui/src/components/Panel/Panel.module.css`:

```css
.panel {
  padding: var(--mp-space-5);
  background: var(--mp-surface-raised);
  border: 1px solid var(--mp-border-subtle);
  border-radius: var(--mp-radius-md);
}
```

`packages/ui/src/components/StatusDot/StatusDot.tsx`:

```tsx
import styles from './StatusDot.module.css';

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting';

const LABELS: Record<ConnectionStatus, string> = {
  connecting: 'Connecting…',
  connected: 'Live',
  reconnecting: 'Reconnecting…',
};

export interface StatusDotProps {
  status: ConnectionStatus;
}

/** Colour is never the only signal: the text label carries the same meaning. */
export function StatusDot({ status }: StatusDotProps) {
  return (
    <span className={styles.status} data-status={status}>
      <span className={styles.dot} aria-hidden="true" />
      {LABELS[status]}
    </span>
  );
}
```

`packages/ui/src/components/StatusDot/StatusDot.module.css`:

```css
.status {
  display: inline-flex;
  align-items: center;
  gap: var(--mp-space-2);
  font-size: 0.8125rem;
  color: var(--mp-text-muted);
}

.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--mp-text-muted);
}

.status[data-status='connected'] .dot {
  background: var(--mp-price-up);
}

.status[data-status='reconnecting'] .dot {
  background: var(--mp-danger);
}
```

`packages/ui/src/components/VisuallyHidden/VisuallyHidden.tsx`:

```tsx
import type { ReactNode } from 'react';
import styles from './VisuallyHidden.module.css';

export function VisuallyHidden({ children }: { children: ReactNode }) {
  return <span className={styles.hidden}>{children}</span>;
}
```

`packages/ui/src/components/VisuallyHidden/VisuallyHidden.module.css`:

```css
.hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  padding: 0;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
  border: 0;
}
```

- [ ] **Step 4: Write the token-leak guard test**

This is the automated form of Global Constraint 3.

`packages/ui/src/tokens/noPrimitiveLeak.test.ts`:

```ts
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const componentsDir = fileURLToPath(new URL('../components', import.meta.url));

function cssFilesUnder(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) return cssFilesUnder(path);
    return path.endsWith('.css') ? [path] : [];
  });
}

/** Primitive tokens and raw hex are the two ways a component can escape the semantic layer. */
const FORBIDDEN = /--mp-(grey|green|red|blue)-|#[0-9a-fA-F]{3,8}\b/;

describe('component stylesheets', () => {
  const files = cssFilesUnder(componentsDir);

  it('finds stylesheets to check', () => {
    expect(files.length).toBeGreaterThan(0);
  });

  it.each(files)('%s references only semantic tokens', (file) => {
    const offending = readFileSync(file, 'utf8')
      .split('\n')
      .filter((line) => FORBIDDEN.test(line));

    expect(offending).toEqual([]);
  });
});
```

- [ ] **Step 5: Export everything from the barrel**

`packages/ui/src/index.ts`:

```ts
export { Alert, type AlertProps } from './components/Alert/Alert';
export { Button, type ButtonProps } from './components/Button/Button';
export { Panel, type PanelProps } from './components/Panel/Panel';
export {
  StatusDot,
  type ConnectionStatus,
  type StatusDotProps,
} from './components/StatusDot/StatusDot';
export { TextField, type TextFieldProps } from './components/TextField/TextField';
export { VisuallyHidden } from './components/VisuallyHidden/VisuallyHidden';
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `pnpm --filter @marketpulse/ui test && pnpm --filter @marketpulse/ui typecheck`
Expected: PASS. The leak guard reports one case per stylesheet, all green.

- [ ] **Step 7: Prove the guard actually guards**

Temporarily change `Alert.module.css`'s `--mp-danger` to `#f87171`, re-run, and confirm the leak test **fails** naming that file. Revert, re-run, confirm green. A guard nobody has watched fail is a guard nobody knows works.

- [ ] **Step 8: Commit**

```bash
git add packages/ui/src
git commit -m "feat: add Alert, Panel, StatusDot and VisuallyHidden primitives

Adds the guard that enforces the semantic-token rule: component CSS may not
reference a primitive token or a hex literal. Verified by mutation."
```

---

### Task 4: Tick direction in the stream reducer

**Files:**
- Modify: `apps/dashboard/src/features/prices/streamReducer.ts`
- Modify: `apps/dashboard/src/features/prices/streamReducer.test.ts`

**Interfaces:**
- Consumes: nothing new.
- Produces: `StreamState.prices[ticker]` becomes `{ price: number; receivedAt: number; direction: TickDirection; seq: number }` where `type TickDirection = 'up' | 'down' | 'neutral'`. `seq` starts at 1 for a ticker's first tick and increments per tick. Task 5 consumes both fields.

**Note on the existing suite:** `streamReducer.test.ts:18` asserts
`expect(next.prices['IVV']).toEqual({ price: 62.4, receivedAt: 1000 })`. `toEqual` is an
exact-shape match, so it fails the moment fields are added. Updating it is part of this task,
not a surprise later.

- [ ] **Step 1: Write the failing tests**

Add to `apps/dashboard/src/features/prices/streamReducer.test.ts`, inside the existing
`describe('streamReducer', ...)` block:

```ts
  it('marks the first tick for a ticker as neutral', () => {
    const next = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    expect(next.prices['IVV']?.direction).toBe('neutral');
    expect(next.prices['IVV']?.seq).toBe(1);
  });

  it('marks a higher price as up', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 62.9, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.direction).toBe('up');
    expect(second.prices['IVV']?.seq).toBe(2);
  });

  it('marks a lower price as down', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 61.0, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.direction).toBe('down');
  });

  it('marks an unchanged price as neutral rather than implying movement', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.direction).toBe('neutral');
    expect(second.prices['IVV']?.seq).toBe(2);
  });

  it('tracks direction per ticker independently', () => {
    let state = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    state = streamReducer(state, {
      type: 'tick', ticker: 'NDQ', price: 30.0, receivedAt: 1000,
    });
    state = streamReducer(state, {
      type: 'tick', ticker: 'IVV', price: 63.0, receivedAt: 2000,
    });

    expect(state.prices['IVV']?.direction).toBe('up');
    expect(state.prices['NDQ']?.direction).toBe('neutral');
  });
```

Then update the existing exact-shape assertion at line 18 to match the new shape:

```ts
    expect(next.prices['IVV']).toEqual({
      price: 62.4,
      receivedAt: 1000,
      direction: 'neutral',
      seq: 1,
    });
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `pnpm --filter @marketpulse/dashboard test -- streamReducer`
Expected: FAIL — the new cases report `undefined` for `direction`, and the updated exact-shape assertion reports the missing fields.

- [ ] **Step 3: Implement direction tracking**

Replace the `StreamState` interface and the `tick` case in
`apps/dashboard/src/features/prices/streamReducer.ts`:

```ts
export type TickDirection = 'up' | 'down' | 'neutral';

export interface PriceEntry {
  price: number;
  receivedAt: number;
  direction: TickDirection;
  /** Increments per tick. Exists so the flash animation can be restarted deterministically. */
  seq: number;
}

export interface StreamState {
  status: StreamStatus;
  prices: Record<string, PriceEntry>;
}
```

```ts
    case 'tick': {
      const previous = state.prices[action.ticker];

      // A first tick has nothing to compare against, and an unchanged price did not
      // move — neither should flash, because a flash asserts movement.
      const direction: TickDirection =
        previous === undefined || action.price === previous.price
          ? 'neutral'
          : action.price > previous.price
            ? 'up'
            : 'down';

      return {
        ...state,
        prices: {
          ...state.prices,
          [action.ticker]: {
            price: action.price,
            receivedAt: action.receivedAt,
            direction,
            seq: (previous?.seq ?? 0) + 1,
          },
        },
      };
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `pnpm --filter @marketpulse/dashboard test -- streamReducer`
Expected: PASS — 13 tests in the file (8 existing + 5 new).

- [ ] **Step 5: Run the whole dashboard suite**

Run: `pnpm --filter @marketpulse/dashboard test`
Expected: PASS. `PriceCell.test.tsx` constructs a `StreamState` literal and will now fail to
typecheck if the fields are missing — if it does, add `direction: 'neutral', seq: 1` to that
fixture and no more; its assertions are Task 5's problem.

- [ ] **Step 6: Commit**

```bash
git add apps/dashboard/src/features/prices/streamReducer.ts apps/dashboard/src/features/prices/streamReducer.test.ts apps/dashboard/src/features/prices/PriceCell.test.tsx
git commit -m "feat: track tick direction per ticker in the stream reducer

First tick and unchanged price are both neutral: a flash asserts movement, and
neither case moved. seq exists so the flash can be restarted deterministically."
```

---

### Task 5: PriceCell — direction arrow, numerals, flash

**Files:**
- Modify: `apps/dashboard/src/features/prices/PriceCell.tsx`
- Modify: `apps/dashboard/src/features/prices/PriceCell.test.tsx`
- Create: `apps/dashboard/src/features/prices/PriceCell.module.css`

**Interfaces:**
- Consumes: `TickDirection` from Task 4.
- Produces: `PriceCell({ ticker, price, stale, disconnected, direction, seq })` — `direction` defaults to `'neutral'` and `seq` to `0` so existing call sites keep compiling until Task 8 passes them.

**Two constraints from Global Constraints 4 and 5:** the element labelled `{TICKER} price`
keeps that exact accessible name and must contain only the formatted price. The arrow is a
**sibling**, `aria-hidden`, so screen readers do not announce "up arrow dollar sixty-two".

- [ ] **Step 1: Write the failing tests**

Replace the body of `apps/dashboard/src/features/prices/PriceCell.test.tsx`'s describe block
with the staleness test rewritten off `toHaveStyle` (jsdom applies no CSS Module styles, so
that assertion cannot survive the move off inline styles), and add the direction tests:

```tsx
describe('PriceCell live staleness', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('marks itself stale once the threshold elapses with no further ticks', () => {
    const receivedAt = Date.now();
    const state: StreamState = {
      status: 'connected',
      prices: { IVV: { price: 62.4, receivedAt, direction: 'neutral', seq: 1 } },
    };

    render(<Harness state={state} ticker="IVV" />);

    const cell = screen.getByLabelText('IVV price');
    expect(cell).toHaveAttribute('data-stale', 'false');

    act(() => {
      vi.advanceTimersByTime(10_000);
    });

    expect(cell).toHaveAttribute('data-stale', 'true');
    expect(cell).toHaveAttribute('title', 'No recent update');
  });
});

describe('PriceCell direction', () => {
  it('shows only the formatted price inside the labelled element', () => {
    render(
      <PriceCell ticker="IVV" price={62.4} stale={false} disconnected={false}
        direction="up" seq={2} />,
    );

    // E2E asserts /^\$\d/ against this element's text — an arrow inside it would break that.
    expect(screen.getByLabelText('IVV price')).toHaveTextContent(/^\$62\.40$/);
  });

  it('renders the direction arrow outside the labelled element and hides it', () => {
    const { container } = render(
      <PriceCell ticker="IVV" price={62.4} stale={false} disconnected={false}
        direction="up" seq={2} />,
    );

    const arrow = container.querySelector('[aria-hidden="true"]');
    expect(arrow).not.toBeNull();
    expect(screen.getByLabelText('IVV price')).not.toContainElement(arrow as HTMLElement);
  });

  it('renders no arrow for a neutral tick', () => {
    const { container } = render(
      <PriceCell ticker="IVV" price={62.4} stale={false} disconnected={false}
        direction="neutral" seq={1} />,
    );

    expect(container.querySelector('[aria-hidden="true"]')).toBeNull();
  });

  it('exposes the direction for styling', () => {
    const { container } = render(
      <PriceCell ticker="IVV" price={61.0} stale={false} disconnected={false}
        direction="down" seq={3} />,
    );

    expect(container.querySelector('[data-direction="down"]')).not.toBeNull();
  });

  it('renders an em dash before the first tick arrives', () => {
    render(
      <PriceCell ticker="IVV" price={undefined} stale disconnected={false}
        direction="neutral" seq={0} />,
    );

    expect(screen.getByLabelText('IVV price')).toHaveTextContent('—');
  });
});
```

Add `PriceCell` to the file's imports if it is not already there, and keep the existing
`Harness` component, updating its `PriceCell` usage to pass
`direction={state.prices[ticker]?.direction ?? 'neutral'}` and `seq={state.prices[ticker]?.seq ?? 0}`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `pnpm --filter @marketpulse/dashboard test -- PriceCell`
Expected: FAIL — `data-stale` is absent (the component still sets inline opacity) and the
`direction`/`seq` props do not exist.

- [ ] **Step 3: Implement PriceCell**

`apps/dashboard/src/features/prices/PriceCell.tsx`:

```tsx
import { memo } from 'react';
import type { TickDirection } from './streamReducer';
import styles from './PriceCell.module.css';

const ARROWS: Record<TickDirection, string> = { up: '▲', down: '▼', neutral: '' };

interface PriceCellProps {
  ticker: string;
  price: number | undefined;
  stale: boolean;
  disconnected: boolean;
  direction?: TickDirection;
  seq?: number;
}

export const PriceCell = memo(function PriceCell({
  ticker,
  price,
  stale,
  disconnected,
  direction = 'neutral',
  seq = 0,
}: PriceCellProps) {
  const dimmed = stale || disconnected;

  return (
    <span className={styles.cell} data-direction={direction}>
      {direction !== 'neutral' && (
        // Keyed on seq so React remounts this leaf on every tick, restarting the CSS
        // animation deterministically. aria-hidden: the arrow is decoration, and the
        // labelled price element below is what assistive technology reads.
        <span key={seq} className={styles.arrow} aria-hidden="true">
          {ARROWS[direction]}
        </span>
      )}
      <span
        className={styles.price}
        aria-label={`${ticker} price`}
        data-stale={String(dimmed)}
        title={disconnected ? 'Reconnecting…' : stale ? 'No recent update' : undefined}
      >
        {price === undefined ? '—' : `$${price.toFixed(2)}`}
      </span>
    </span>
  );
});
```

- [ ] **Step 4: Write the stylesheet**

`apps/dashboard/src/features/prices/PriceCell.module.css`:

```css
.cell {
  display: inline-flex;
  align-items: baseline;
  gap: var(--mp-space-1);
}

.price {
  font-family: var(--mp-font-mono);
  /* Prices tick constantly; without tabular figures the row jitters as digits change. */
  font-variant-numeric: tabular-nums;
  color: var(--mp-price-neutral);
  transition: opacity 200ms ease;
}

.price[data-stale='true'] {
  opacity: var(--mp-opacity-stale);
}

.arrow {
  font-size: 0.75rem;
  animation: flash 600ms ease-out;
}

.cell[data-direction='up'] .arrow {
  color: var(--mp-price-up);
}

.cell[data-direction='down'] .arrow {
  color: var(--mp-price-down);
}

.cell[data-direction='up'] .price {
  animation: flash-up 600ms ease-out;
}

.cell[data-direction='down'] .price {
  animation: flash-down 600ms ease-out;
}

@keyframes flash {
  from { opacity: 0; }
  to { opacity: 1; }
}

@keyframes flash-up {
  from { color: var(--mp-price-up); }
  to { color: var(--mp-price-neutral); }
}

@keyframes flash-down {
  from { color: var(--mp-price-down); }
  to { color: var(--mp-price-neutral); }
}

/*
 * Motion off: the flash goes, the arrow and its colour stay. No information is
 * carried by the animation alone.
 */
@media (prefers-reduced-motion: reduce) {
  .arrow,
  .cell[data-direction='up'] .price,
  .cell[data-direction='down'] .price {
    animation: none;
  }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `pnpm --filter @marketpulse/dashboard test -- PriceCell`
Expected: PASS — 6 tests.

- [ ] **Step 6: Commit**

```bash
git add apps/dashboard/src/features/prices
git commit -m "feat: give PriceCell a direction arrow, tabular numerals and a flash

The arrow sits outside the labelled price element and is aria-hidden, so the
accessible name stays '{TICKER} price' and its text stays parseable as currency.
Staleness moves from an inline style to a data attribute, removing the app's
last inline style. prefers-reduced-motion drops the animation and keeps the cue."
```

---

### Task 6: The application shell

**Files:**
- Modify: `apps/dashboard/src/App.tsx`, `apps/dashboard/index.html`, `apps/dashboard/src/features/auth/SignOutButton.tsx`
- Create: `apps/dashboard/src/app.module.css`

**Interfaces:**
- Consumes: `Button`, `StatusDot` from `@marketpulse/ui`.
- Produces: the `.page` / `.header` / `.main` layout classes other screens sit inside. No exported TypeScript.

**Note:** `usePriceStream` is called by `WatchlistScreen`, not by `App`, and moving it would
change when the SignalR connection opens. The header's status indicator is therefore added in
Task 8, inside the screen that already owns the stream. This task lays out the shell only.

- [ ] **Step 1: Declare the colour scheme**

In `apps/dashboard/index.html`, add inside `<head>` after the viewport meta:

```html
    <meta name="color-scheme" content="dark" />
```

Without it the browser renders native scrollbars, form controls and autofill backgrounds
light against the dark page.

- [ ] **Step 2: Write the shell stylesheet**

`apps/dashboard/src/app.module.css`:

```css
.page {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--mp-space-4);
  padding: var(--mp-space-3) var(--mp-space-5);
  border-bottom: 1px solid var(--mp-border-subtle);
  background: var(--mp-surface-raised);
}

.wordmark {
  margin: 0;
  font-size: 1rem;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.session {
  display: flex;
  align-items: center;
  gap: var(--mp-space-3);
}

.email {
  font-size: 0.8125rem;
  color: var(--mp-text-muted);
}

.main {
  width: 100%;
  max-width: 60rem;
  margin: 0 auto;
  padding: var(--mp-space-6) var(--mp-space-5);
}

@media (max-width: 720px) {
  .header,
  .main {
    padding-left: var(--mp-space-3);
    padding-right: var(--mp-space-3);
  }
}
```

- [ ] **Step 3: Apply it in App.tsx**

Replace the JSX inside `BrowserRouter` in `apps/dashboard/src/App.tsx`:

```tsx
        <div className={styles.page}>
          <header className={styles.header}>
            <h1 className={styles.wordmark}>MarketPulse Pro</h1>
            <SignOutButton />
          </header>
          <main className={styles.main}>
            <Routes>
              <Route path="/login" element={<LoginScreen />} />
              <Route path="/register" element={<RegisterScreen />} />
              <Route
                path="/"
                element={
                  <ProtectedRoute>
                    <WatchlistScreen />
                  </ProtectedRoute>
                }
              />
            </Routes>
          </main>
        </div>
```

Add the import at the top: `import styles from './app.module.css';`

- [ ] **Step 4: Rebuild SignOutButton on the primitives**

`apps/dashboard/src/features/auth/SignOutButton.tsx` — keep the `if (!session) return null;`
guard and the button's accessible name `Sign out` exactly:

```tsx
import { Button } from '@marketpulse/ui';
import { useNavigate } from 'react-router-dom';
import styles from '../../app.module.css';
import { useLogout, useSession } from './useSession';

export function SignOutButton() {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();

  if (!session) return null;

  return (
    <div className={styles.session}>
      <span className={styles.email}>{session.email}</span>
      <Button
        variant="ghost"
        onClick={() => logout.mutate(undefined, { onSuccess: () => navigate('/login') })}
      >
        Sign out
      </Button>
    </div>
  );
}
```

- [ ] **Step 5: Verify**

Run: `pnpm --filter @marketpulse/dashboard test && pnpm -r typecheck`
Expected: PASS — all 20 existing dashboard tests still green.

- [ ] **Step 6: Commit**

```bash
git add apps/dashboard/index.html apps/dashboard/src/App.tsx apps/dashboard/src/app.module.css apps/dashboard/src/features/auth/SignOutButton.tsx
git commit -m "feat: lay out the application shell on the token system

Adds color-scheme: dark so native controls and scrollbars follow the app."
```

---

### Task 7: The authentication screens

**Files:**
- Modify: `apps/dashboard/src/features/auth/LoginScreen.tsx`, `RegisterScreen.tsx`
- Create: `apps/dashboard/src/features/auth/AuthScreen.module.css`

**Interfaces:**
- Consumes: `Alert`, `Button`, `Panel`, `TextField` from `@marketpulse/ui`.
- Produces: nothing consumed downstream.

**Do not change:** the headings `Sign in` and `Create an account`, the submit button names
`Sign in` and `Create account`, the labels `Email` and `Password`, the input `id`s, or the
`role="alert"` error rendering. Two E2E specs and four component tests depend on them.

- [ ] **Step 1: Write the stylesheet**

`apps/dashboard/src/features/auth/AuthScreen.module.css`:

```css
.screen {
  max-width: 24rem;
  margin: var(--mp-space-8) auto;
}

.heading {
  margin: 0 0 var(--mp-space-5);
  font-size: 1.25rem;
}

.form {
  display: flex;
  flex-direction: column;
  gap: var(--mp-space-4);
}

.footer {
  margin: var(--mp-space-5) 0 0;
  font-size: 0.875rem;
  color: var(--mp-text-muted);
}

.footer a {
  color: var(--mp-accent);
}

.error {
  margin-top: var(--mp-space-4);
}
```

- [ ] **Step 2: Rebuild LoginScreen**

`apps/dashboard/src/features/auth/LoginScreen.tsx`:

```tsx
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
```

- [ ] **Step 3: Rebuild RegisterScreen**

`apps/dashboard/src/features/auth/RegisterScreen.tsx` — the password hint moves into
`TextField`'s `hint` prop, which reproduces the existing `aria-describedby` wiring:

```tsx
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
```

- [ ] **Step 4: Run the tests to verify nothing regressed**

Run: `pnpm --filter @marketpulse/dashboard test && pnpm -r typecheck`
Expected: PASS — `LoginScreen.test.tsx` (2 tests) and `ProtectedRoute.test.tsx` (2 tests)
still green, unmodified. They query by role and label, which is exactly why they survive.

- [ ] **Step 5: Commit**

```bash
git add apps/dashboard/src/features/auth
git commit -m "feat: rebuild the auth screens on the ui primitives

Headings, button names, labels and input ids are unchanged: four component tests
and two E2E specs assert them."
```

---

### Task 8: The watchlist table

**Files:**
- Modify: `apps/dashboard/src/features/watchlist/WatchlistScreen.tsx`
- Create: `apps/dashboard/src/features/watchlist/WatchlistScreen.module.css`
- Modify: `tests/e2e/specs/authentication.spec.ts`

**Interfaces:**
- Consumes: `Alert`, `Button`, `Panel`, `StatusDot`, `TextField`; `PriceCell` with `direction`/`seq` from Task 5.
- Produces: nothing consumed downstream.

**The vacuous-assertion migration.** `tests/e2e/specs/authentication.spec.ts:21` asserts
`await expect(page.getByRole('listitem')).toHaveCount(0)`. Once rows stop being `<li>` no
listitem can ever exist, so that assertion passes forever and proves nothing. It becomes a
row count. **Do not skip Step 5, which proves the replacement can fail.**

- [ ] **Step 1: Write the stylesheet**

`apps/dashboard/src/features/watchlist/WatchlistScreen.module.css`:

```css
.header {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: var(--mp-space-4);
  margin-bottom: var(--mp-space-4);
}

.heading {
  margin: 0;
  font-size: 1.25rem;
}

.addForm {
  display: flex;
  align-items: flex-end;
  gap: var(--mp-space-2);
  margin-bottom: var(--mp-space-4);
}

.tableWrap {
  overflow-x: auto;
}

.table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9375rem;
}

.table th {
  padding: var(--mp-space-2) var(--mp-space-3);
  border-bottom: 1px solid var(--mp-border-subtle);
  background: var(--mp-surface-raised);
  color: var(--mp-text-muted);
  font-size: 0.75rem;
  font-weight: 500;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  text-align: left;
}

.table td {
  padding: var(--mp-space-3);
  border-bottom: 1px solid var(--mp-border-subtle);
}

.table tbody tr:hover {
  background: var(--mp-surface-hover);
}

.ticker {
  font-family: var(--mp-font-mono);
  font-weight: 600;
}

.numeric {
  text-align: right;
}

.actions {
  width: 1%;
  text-align: right;
}

.empty {
  padding: var(--mp-space-8) var(--mp-space-4);
  text-align: center;
  color: var(--mp-text-muted);
}
```

- [ ] **Step 2: Rebuild the screen**

`apps/dashboard/src/features/watchlist/WatchlistScreen.tsx`:

```tsx
import { Alert, Button, Panel, StatusDot, TextField } from '@marketpulse/ui';
import { useState, type FormEvent } from 'react';
import { PriceCell } from '../prices/PriceCell';
import { isStale } from '../prices/streamReducer';
import { useNow } from '../prices/useNow';
import { usePriceStream } from '../prices/usePriceStream';
import styles from './WatchlistScreen.module.css';
import { useAddItem, useRemoveItem, useWatchlist } from './useWatchlist';

export function WatchlistScreen() {
  const { data, isPending, isError } = useWatchlist();
  const addItem = useAddItem();
  const removeItem = useRemoveItem();
  const [ticker, setTicker] = useState('');
  const stream = usePriceStream();
  const now = useNow();

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const value = ticker.trim();
    if (value === '') return;

    addItem.mutate(value, { onSuccess: () => setTicker('') });
  }

  if (isPending) return <p>Loading watchlist…</p>;
  if (isError) return <p role="alert">Could not load your watchlist.</p>;

  return (
    <section aria-labelledby="watchlist-heading">
      <div className={styles.header}>
        <h2 className={styles.heading} id="watchlist-heading">
          Watchlist
        </h2>
        <StatusDot status={stream.status} />
      </div>

      {stream.status === 'reconnecting' && (
        <p role="status" className={styles.empty}>
          Reconnecting to the price feed…
        </p>
      )}

      <form className={styles.addForm} onSubmit={handleSubmit}>
        <TextField
          id="add-ticker"
          label="Add ticker"
          value={ticker}
          onChange={(e) => setTicker(e.target.value)}
          maxLength={8}
        />
        <Button type="submit" disabled={addItem.isPending}>
          Add
        </Button>
      </form>

      {addItem.isError && (
        <Alert>
          {addItem.error.message}
          {addItem.error.correlationId && ` (ref: ${addItem.error.correlationId})`}
        </Alert>
      )}

      <Panel className={styles.tableWrap}>
        {data.items.length === 0 ? (
          <p className={styles.empty}>No tickers yet. Add one above to start streaming prices.</p>
        ) : (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">Ticker</th>
                <th scope="col" className={styles.numeric}>
                  Last
                </th>
                <th scope="col" className={styles.actions}>
                  <span className={styles.srOnly}>Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((item) => {
                const entry = stream.prices[item.ticker];

                return (
                  <tr key={item.ticker}>
                    <td className={styles.ticker}>{item.ticker}</td>
                    <td className={styles.numeric}>
                      <PriceCell
                        ticker={item.ticker}
                        price={entry?.price}
                        stale={isStale(stream, item.ticker, now)}
                        disconnected={stream.status === 'reconnecting'}
                        direction={entry?.direction ?? 'neutral'}
                        seq={entry?.seq ?? 0}
                      />
                    </td>
                    <td className={styles.actions}>
                      <Button
                        variant="danger"
                        onClick={() => removeItem.mutate(item.ticker)}
                        aria-label={`Remove ${item.ticker}`}
                      >
                        Remove
                      </Button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </Panel>
    </section>
  );
}
```

Add `.srOnly` to `WatchlistScreen.module.css` (the header cell needs an accessible name
without a visible one):

```css
.srOnly {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  overflow: hidden;
  clip-path: inset(50%);
  white-space: nowrap;
}
```

- [ ] **Step 3: Run the component tests**

Run: `pnpm --filter @marketpulse/dashboard test`
Expected: PASS, with `WatchlistScreen.test.tsx` unmodified. Its two tests query
`getByRole('button', { name: /add/i })` and `getByRole('alert')` and contain no `listitem`
assertion (verified while writing this plan), so the table migration does not touch them.

- [ ] **Step 4: Migrate the vacuous E2E assertion**

In `tests/e2e/specs/authentication.spec.ts`, replace line 21:

```ts
  await expect(page.getByRole('listitem')).toHaveCount(0);
```

with an assertion against the designed empty state, which cannot pass vacuously:

```ts
  // A fresh account has an empty watchlist. Asserting the empty-state copy rather than
  // a zero count means the assertion fails if the table silently stops rendering.
  await expect(page.getByText('No tickers yet. Add one above to start streaming prices.'))
    .toBeVisible();
```

Then after the `Add` click at line 24-25, add a row-count assertion:

```ts
  await expect(page.getByRole('row')).toHaveCount(2); // header row + IVV
```

- [ ] **Step 5: Prove the new assertions can fail**

Temporarily change the empty-state copy in `WatchlistScreen.tsx` to `No tickers.`, run
`pnpm e2e`, and confirm the spec **fails**. Revert and confirm it passes. The assertion it
replaced could not fail under any circumstance, so this step is the whole point of the change.

- [ ] **Step 6: Run the full E2E suite**

Run: `docker compose up -d && dotnet ef database update --project src/MarketPulse.Infrastructure && pnpm --filter @marketpulse/dashboard build && pnpm e2e`
Expected: 3 passed.

- [ ] **Step 7: Commit**

```bash
git add apps/dashboard/src/features/watchlist tests/e2e/specs/authentication.spec.ts
git commit -m "feat: render the watchlist as a table with a designed empty state

Migrates an E2E assertion that would have passed forever once rows stopped being
list items, replacing it with empty-state copy and a row count that can fail."
```

---

### Task 9: Documentation and final verification

**Files:**
- Create: `docs/adr/008-design-tokens.md`
- Modify: `README.md`

**Interfaces:** none.

- [ ] **Step 1: Write ADR-008**

`docs/adr/008-design-tokens.md`, following the house format (Context / Decision / Rationale /
Rejected alternatives / Consequences) used by ADRs 001-003. Numbered 008 because the README
already forward-references 004 (CQRS scope), 005 and 006 (postmortems) and 007 (state
architecture); taking a reserved number would break those links.

Content requirements — each must appear:
- The two-layer structure and the rule that components may reference only semantic tokens,
  with the leak guard named as its enforcement.
- Rejected: Tailwind (styling decisions move into markup, weaker design-system artefact,
  config plus build step); a component library such as MUI or Radix (makes the deliverable
  somebody else's work); runtime CSS-in-JS (injects `<style>`, fights the no-`unsafe-inline`
  CSP goal in phase 6); global BEM CSS (no scoping).
- Consequences: a light theme is a redefinition of the semantic block; every new component
  owes a contrast check; the token test is the enforcement point, not review.
- The honest limitation: **no visual-regression testing exists**, because Storybook was
  deferred. Appearance is reviewed by eye.

- [ ] **Step 2: Update the README frontend section**

In the "Frontend — Feature-sliced monorepo" tree, `packages/ui` is described as
"Design system + Storybook". Correct it to reflect what exists:

```
packages/ui             # Design tokens + primitives (Storybook: not yet)
```

Add below the tree, in the same voice as the surrounding bullets:

```markdown
- **Design tokens in two layers:** primitives hold raw values, semantic tokens hold meaning,
  and components may reference only the semantic layer — enforced by a test, not convention.
  Contrast ratios are computed against WCAG AA in CI rather than eyeballed. See
  [ADR-008](docs/adr/008-design-tokens.md)
```

- [ ] **Step 3: Full verification**

Run each and record the actual output — this is the gate for promoting to `main`:

```bash
pnpm -r typecheck
pnpm -r test
pnpm --filter @marketpulse/dashboard build
pnpm e2e
dotnet build MarketPulse.sln -warnaserror
dotnet test MarketPulse.sln --no-build
```

Expected: typecheck clean across 4 packages; frontend tests green including the new
`@marketpulse/ui` suite; build succeeds; 3 E2E passed; .NET 0 warnings and 63 unit + 41
integration passing (unchanged — this slice touches no .NET code, and that is itself the
evidence the seam held).

- [ ] **Step 4: Confirm the constraints hold**

```bash
grep -rn "style={{" apps/dashboard/src packages/ui/src   # expect no matches
grep -rn "#[0-9a-fA-F]\{3,8\}" packages/ui/src/components  # expect no matches
```

- [ ] **Step 5: Commit**

```bash
git add docs/adr/008-design-tokens.md README.md
git commit -m "docs: add ADR-008 and correct the README frontend section

Records the token architecture and its rejected alternatives, including the
absence of visual-regression testing rather than implying it exists."
```

---

## Self-Review

**Spec coverage.** Every spec section maps to a task: `packages/ui` and tokens → Task 1;
primitives → Tasks 2-3; the semantic-token rule → Task 3's guard; tick direction → Task 4;
`PriceCell` arrow, tabular numerals, flash, reduced motion → Task 5; shell and
`color-scheme` → Task 6; auth screens → Task 7; watchlist table, empty state and the E2E
migration → Task 8; ADR-008, README and verification → Task 9. The spec's contrast
requirement is Task 1's test; the "no inline styles" criterion is Task 5 plus Task 9's grep;
the 720px breakpoint is in Tasks 6 and 8.

**Type consistency.** `TickDirection` is defined once in Task 4 and imported by Task 5.
`PriceEntry` gains `direction` and `seq` in Task 4; Task 5 reads both; Task 8 passes both
from `stream.prices[ticker]`. `ConnectionStatus` in `StatusDot` is structurally identical to
`StreamStatus` (`'connecting' | 'connected' | 'reconnecting'`), so Task 8's
`<StatusDot status={stream.status} />` typechecks without a cast.

**Known ordering hazard.** Task 4 changes `StreamState`, which `PriceCell.test.tsx`
constructs as a literal — Task 4 Step 5 says to add the two fields to that fixture and
nothing else, leaving its assertions to Task 5. Executing Task 5 before Task 4 will not
compile.
