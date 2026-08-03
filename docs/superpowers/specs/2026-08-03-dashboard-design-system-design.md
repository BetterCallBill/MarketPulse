# Slice 3 — Dashboard design system: "It should look like a product"

**Date:** 2026-08-03
**Status:** Approved (design), pending implementation plan
**Previous slice:** [2026-08-02-authentication-design.md](2026-08-02-authentication-design.md)
**Parent spec:** [MarketPulse-Pro-README.md](../../MarketPulse-Pro-README.md)
**Category reference:** [intervew-aspects.md](../../intervew-aspects.md)

---

## Why this slice exists

The dashboard has no CSS. Not thin CSS — none. There is no stylesheet, no class attribute,
and no design token anywhere in `apps/dashboard`. The single piece of styling in the entire
application is `style={{ opacity: dimmed ? 0.4 : 1 }}` in `PriceCell.tsx`. Everything else
renders as browser default: Times New Roman, a bulleted list of tickers, and native buttons.

That was correct for slices 1 and 2. Both were about proving data flows — a watchlist that
persists, prices that tick, sessions that survive a reload — and styling would have been
premature. It stops being correct the moment anyone looks at it.

This slice also pulls **roadmap phase 4** (frontend core, design system) ahead of **phase 3**
(messaging and alerts). That reordering is deliberate and worth stating: phase 3 is the
larger engineering story, but it produces nothing a person can see, and the dashboard is
what a reader opens first. Phase 3 is not cancelled, only resequenced.

Category 6 (HTML/CSS & layout) is one of the three interview categories with no supporting
code at all. This slice opens it.

### Decisions taken, with rejected alternatives

| Decision | Chosen | Rejected because |
|---|---|---|
| Styling mechanism | **CSS Modules over custom-property tokens** | Tailwind pushes styling decisions into markup and makes `packages/ui` a thin wrapper over utilities — a weaker answer to category 6 than a token layer we designed, and it adds config plus a build step. Global BEM CSS has no scoping, so collisions grow with the app. Runtime CSS-in-JS (styled-components, emotion) injects `<style>` elements and fights the phase 6 goal of a CSP with no `unsafe-inline` |
| Token structure | **Two layers: primitives, then semantic aliases** | A single flat layer of named colours works until the second theme, at which point every component that referenced `--mp-green-400` has to be found and rewritten. The indirection is the entire point of the exercise |
| Theme scope | **Dark only, structured so light is cheap** | Building both now doubles the palette work to serve a preference nobody has expressed. Building dark with hardcoded values would make light expensive later. The semantic layer is the hedge |
| Price movement | **Tick direction only** (up/down versus previous tick) | The feed carries `ticker`, `price`, `timestampUtc` and nothing else. Day-change % needs a previous close the API does not have; inventing a column the data cannot fill is dishonest. A session-baseline delta is derivable but resets on every reload, which reads as a bug to anyone expecting day change |
| Component library | **Hand-built primitives, ~6 of them** | MUI, Chakra or Radix would be faster and would make the design-system deliverable somebody else's work. The point of `packages/ui` here is the token architecture, which a component library supplies ready-made and therefore teaches nothing |
| Storybook | **Deferred to a follow-up** | The README promises it and it should eventually exist, but it is a heavy dependency, a config surface, and a CI job — added in a slice whose stated purpose is that the app looks bad. Primitives are covered by the existing Vitest + RTL setup regardless, so deferring it defers documentation, not testing |
| Virtualization | **Not now**, despite the README listing `@tanstack/react-virtual` | Virtualizing a handful of rows solves a problem this app does not have. Documented as a deliberate omission with the trigger for revisiting it (roughly 100+ rows) |
| Table semantics | **Real `<table>`** | A grid of `<div>`s with ARIA roles reimplements what the browser already provides correctly, and gets the keyboard and screen-reader behaviour subtly wrong |

---

## Scope boundary

### In scope

| Area | Deliverable |
|---|---|
| `packages/ui` | New workspace package: `tokens.css` (primitive + semantic layers) and ~6 primitives with colocated CSS Modules. No build step — ships TS source like `api-client` |
| Shell | `App.tsx` header gains layout, a connection-status indicator driven by `stream.status`, and a max-width centred `main` |
| Watchlist | `<ul>` becomes a semantic `<table>`; inline add-ticker form; designed empty state; row hover and remove affordance |
| Prices | `streamReducer` gains per-ticker tick direction; `PriceCell` gains an `aria-hidden` direction arrow, tabular numerals, and a flash animation that honours `prefers-reduced-motion` |
| Auth screens | Login and register become centred cards built from the new primitives; errors render through the `Alert` primitive |
| Styling hygiene | The last inline style (`PriceCell`'s opacity) moves to a token-driven class; `index.html` declares `color-scheme: dark` |
| Testing | TDD on reducer direction logic; RTL tests for arrow semantics and accessible names; migration of one vacuous E2E assertion |
| Docs | **ADR-008** (design tokens and the CSS Modules choice, with rejected alternatives); README frontend section updated to match what exists. Numbered 008 because the README already forward-references 004 (CQRS scope), 005 and 006 (postmortems) and 007 (state architecture); none are written yet, but taking a reserved number would silently break those links |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

- **Light theme.** The semantic layer makes it a follow-up, not a rewrite.
- **Storybook and visual-regression testing.** There is nowhere to hang a snapshot without
  Storybook, so styling correctness in this slice is reviewed by eye. Stated plainly rather
  than implied to be covered.
- **New API fields.** No previous close, no volume, no OHLC. Any column requiring them is
  out until the API carries them.
- **Charts and sparklines.** They need a price history endpoint that does not exist.
- **`apps/alerts-mfe` and Module Federation.** Belongs with the slice that has alerts to show.
- **Zustand.** The README's two-layer state architecture names it, but this slice introduces
  no client state that `useState` and the existing reducer do not already handle. Adding a
  store to justify a README line is backwards.
- **Mobile-first responsive design.** Desktop-first, with a single breakpoint at **720px**:
  below it, padding tightens and the table scrolls horizontally inside its own container
  rather than the page. No stacked-card row layout, no navigation drawer.

---

## Architecture

### Package layout

`packages/ui` mirrors `packages/api-client`: `main` and `types` point at `./src/index.ts`,
there is no build step, and Vite compiles it from source. CSS Modules resolve through the
consuming app's Vite pipeline with no additional configuration.

```
packages/ui/src/
  index.ts                          # barrel export
  tokens/tokens.css                 # imported once, by the dashboard entry point
  components/
    Button/Button.tsx + Button.module.css
    TextField/TextField.tsx + TextField.module.css
    Alert/Alert.tsx + Alert.module.css
    Panel/Panel.tsx + Panel.module.css
    StatusDot/StatusDot.tsx + StatusDot.module.css
    VisuallyHidden/VisuallyHidden.tsx
```

The watchlist table is **not** a `packages/ui` primitive. It is specific to this domain and
stays in `apps/dashboard/src/features/watchlist/`. The boundary: `packages/ui` holds what a
second application would reuse unchanged; anything that knows what a ticker is does not
qualify.

### The token layer

Two layers, and one rule that gives them their value.

**Primitives** carry raw values and no meaning:

```css
--mp-grey-950: #0b0e13;   --mp-grey-900: #12161d;   --mp-grey-850: #1a1f28;
--mp-grey-700: #2a313d;   --mp-grey-400: #8b95a5;   --mp-grey-200: #c9d1dc;
--mp-grey-50:  #f2f5f9;   --mp-green-400: #4ade80;  --mp-red-400: #f87171;
--mp-blue-400: #60a5fa;
--mp-space-1: 0.25rem;  /* 4px base scale, 1–8 */
--mp-radius-sm: 4px;    --mp-radius-md: 8px;
--mp-font-ui: system-ui, -apple-system, "Segoe UI", sans-serif;
--mp-font-mono: ui-monospace, SFMono-Regular, "SF Mono", Menlo, monospace;
```

**Semantic tokens** reference primitives and carry meaning:

```css
--mp-surface-base: var(--mp-grey-950);      --mp-surface-raised: var(--mp-grey-900);
--mp-surface-hover: var(--mp-grey-850);     --mp-border-subtle: var(--mp-grey-700);
--mp-text-primary: var(--mp-grey-50);       --mp-text-secondary: var(--mp-grey-200);
--mp-text-muted: var(--mp-grey-400);        --mp-accent: var(--mp-blue-400);
--mp-price-up: var(--mp-green-400);         --mp-price-down: var(--mp-red-400);
--mp-price-neutral: var(--mp-text-primary); --mp-danger: var(--mp-red-400);
--mp-focus-ring: var(--mp-blue-400);        --mp-opacity-stale: 0.45;
```

**The rule: components may only reference semantic tokens.** Never a primitive, never a
literal colour. A light theme then means redefining the semantic block under a selector,
touching no component. Any violation is a review defect, and the implementation plan should
call for a grep over `packages/ui/src/components` proving no component CSS references
`--mp-grey-`, `--mp-green-`, `--mp-red-`, `--mp-blue-`, or a hex literal.

**Contrast is verified, not eyeballed.** Every text-on-surface pair must be computed and
recorded against WCAG AA — 4.5:1 for body text, 3:1 for large text and UI boundaries — with
the measured ratio written into the implementation report. The values above are chosen to
clear it, but "chosen to" is not evidence. Phase 6 targets zero axe violations; guessing
here just moves the work later.

### Ticking numbers

Two details specific to a table of live prices:

- **`font-variant-numeric: tabular-num`** on every price, so a row does not jitter as digit
  widths change between ticks.
- **`color-scheme: dark`** in `index.html`, so native scrollbars, form controls and the
  browser's own UI follow the app instead of rendering light against a dark page.

### Tick direction

`StreamState.prices` currently holds `{ price, receivedAt }` per ticker. It gains:

```ts
prices: Record<string, {
  price: number;
  receivedAt: number;
  direction: 'up' | 'down' | 'neutral';
  seq: number;
}>
```

`direction` compares the incoming price against the one it replaces. The **first** tick for a
ticker is `neutral` (there is nothing to compare against), and an **unchanged** price is
`neutral` (a flash implying movement where there was none is a lie). `seq` increments per
tick and exists solely to restart the flash animation deterministically: the flash element is
keyed on `seq`, so React remounts that one leaf node and the CSS animation replays. This is
pure reducer logic with no DOM involvement, so it is specified test-first.

### Accessibility, and two constraints the E2E suite imposes

The existing markup is already good — landmarks, labelled inputs, `role="alert"`,
`aria-label` on price cells — and every Playwright selector is role- or label-based. Two of
them constrain the visual work directly:

1. **`getByLabel('IVV price')` is asserted to have text matching `/^\$\d/`.** An arrow glyph
   placed inside that element would break it, and would also make a screen reader announce
   "up arrow dollar one two three". The arrow therefore lives in a **sibling** element marked
   `aria-hidden`, and the labelled price element keeps its exact accessible name and text.

2. **`getByRole('listitem')).toHaveCount(0)` asserts an empty watchlist.** Once rows stop
   being `<li>`, no listitem can ever exist, so that assertion **passes forever and proves
   nothing**. A vacuous pass is worse than a break, because nothing announces it. It migrates
   to counting `row` roles, and that migration is part of this slice's work.

Beyond those:

- **Colour is never the only cue.** Direction is carried by an arrow as well as by colour,
  which matters for the ~8% of men with red-green colour vision deficiency — the exact
  population a green-up/red-down convention fails.
- **`prefers-reduced-motion: reduce`** drops the flash animation. Arrow and colour remain, so
  no information is lost with motion off.
- **Focus is always visible.** A `:focus-visible` ring built from `--mp-focus-ring`; outlines
  are never removed without replacement.
- The connection-status indicator keeps a text label, not colour alone, and retains the
  existing `role="status"` announcement for reconnection.

### Screens

**Shell** (`App.tsx`) — header with wordmark, connection-status indicator, and the existing
session controls, which continue to render only when a session exists (`SignOutButton`
already returns `null` without one). `main` is width-constrained and centred.

**Watchlist** — an inline add-ticker form above a `<table>` of Ticker · Last · Updated ·
remove. Dense rows, raised header, subtle separators, hover state. The empty state is
designed copy rather than an empty `<ul>`. Staleness dims a row through a token-driven class.

**Login and register** — centred `Panel` cards using `TextField`, `Button` and `Alert`. The
headings `Sign in` and `Create account` and the button labels are asserted by E2E and must
not change.

---

## Error handling

No new error paths. The existing ones get a visual home instead of an unstyled paragraph:
`addItem.isError` and the auth failures render through `Alert`, which keeps `role="alert"`
and the correlation-id suffix the API already returns. `Alert` styles the `danger` tone from
tokens; it does not own retry behaviour or messaging, which stay where they are.

---

## Testing

| Level | What it covers |
|---|---|
| Unit (Vitest) | `streamReducer` direction: first tick is neutral, higher is up, lower is down, unchanged is neutral, and `seq` increments per tick. Written test-first — this is the only new logic in the slice |
| Component (RTL) | The direction arrow is `aria-hidden`; the price element's accessible name and text are unchanged by the arrow's presence; `Alert` renders with `role="alert"`; `TextField` associates label and input; the existing 20 dashboard tests stay green untouched |
| E2E (Playwright) | The listitem assertion migrates to row roles. Every other selector is unchanged, which is itself the evidence that semantics survived the restyle |
| Not covered | Visual regression. Without Storybook there is nowhere to hang snapshots, so appearance is reviewed by eye this slice. Recorded as a gap, not implied to be covered |

---

## Done criteria

- [ ] `packages/ui` exists, exports the primitives, and ships no build step
- [ ] No component CSS references a primitive token or a hex literal (grep-verified)
- [ ] Every text-on-surface pair's contrast ratio measured and recorded against WCAG AA
- [ ] No inline `style` attribute remains in `apps/dashboard`
- [ ] `prefers-reduced-motion: reduce` disables the flash with arrow and colour retained
- [ ] The listitem E2E assertion is migrated and demonstrably non-vacuous
- [ ] `pnpm -r typecheck`, `pnpm -r test`, and `pnpm e2e` all pass
- [ ] ADR-008 written with rejected alternatives; README frontend section matches reality

---

## Interview category coverage

| Category | Advanced how |
|---|---|
| 6 · HTML/CSS & layout | Opened from nothing: token architecture, CSS Modules scoping, semantic table markup, contrast verified against WCAG AA rather than assumed |
| 5 · Frontend framework depth | `PriceCell`'s existing `memo` boundary now has a real reason to exist; the animation-restart-by-key technique is a deliberate reconciliation decision |
| 3 · TypeScript | The direction union is exhaustively narrowed, matching the existing `never`-check pattern in `streamReducer` |
| 9 · Accessibility | First genuine work in this category: reduced-motion, non-colour-dependent state, focus-visible, screen-reader-safe decorative content |
| 2 · JavaScript fundamentals | Unchanged. This slice adds no async or language-internals material and should not be claimed for it |

---

## What comes next

**Phase 3 — messaging and alerts** (RabbitMQ, outbox, extracted `MarketPulse.Alerts` worker,
chaos test), resequenced behind this slice rather than dropped. It remains the largest gap in
the codebase and the whole of category 11.

Follow-ups this slice deliberately leaves open: Storybook over the primitives, a light theme
built by redefining the semantic layer, and virtualization if a watchlist ever grows past
roughly 100 rows.
