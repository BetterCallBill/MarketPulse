# ADR-008: Two-layer design tokens, enforced by a test

**Status:** Accepted · **Date:** 2026-08-03

## Context

Before this slice, `apps/dashboard` had no CSS at all: no stylesheet, no class attribute,
and the single piece of styling in the app was an inline `style={{ opacity: ... }}` on
`PriceCell`. Category 6 (HTML/CSS & layout) had no supporting code, and the dashboard read
as unstyled to anyone who opened it. `packages/ui` needed a styling mechanism and a token
structure that could carry a dark theme now without making a second theme expensive later.

## Decision

`packages/ui/src/tokens/tokens.css` defines custom properties in two layers:

- **Primitives** hold raw values and carry no meaning — the grey/green/red/blue colour
  ramps, the 4px space scale, radii, and font stacks (`--mp-grey-950`, `--mp-green-400`,
  `--mp-space-4`, and so on).
- **Semantic tokens** reference primitives and carry meaning — `--mp-surface-base`,
  `--mp-text-primary`, `--mp-price-up`, `--mp-focus-ring`. Each resolves through a `var()`
  chain down to a primitive literal.

Components may reference only the semantic layer. That indirection is what makes a second
theme a redefinition of the semantic block in `tokens.css` rather than a hunt through every
component for a hardcoded primitive or hex value. The rule is enforced by
`noPrimitiveLeak.test.ts`, which walks every CSS Module under `packages/ui/src/components`
and fails if a line matches a primitive token name or a raw hex literal — a leak guard, not
a convention documented in a comment that nobody reads.

Component styling itself is CSS Modules, colocated with each component, consuming only the
semantic custom properties.

## Rationale

Category 6 is one of three interview categories with no prior code, and a token layer we
designed ourselves is a stronger answer to it than a dependency that supplies the same
outcome ready-made. The two-layer split is the standard fix for the failure mode of a flat
palette: with one layer, `--mp-green-400` used directly in a dozen components means a theme
change is a grep-and-replace across the codebase and an audit of every place a colour choice
also encoded a semantic decision (this green means "price went up", not "brand accent").
With two layers, the semantic block is the only thing a second theme touches.

Enforcing the rule with a test rather than a lint rule or review checklist was deliberate:
`noPrimitiveLeak.test.ts` runs in the same `pnpm -r test` gate as everything else, so a
primitive leak fails CI the same way a broken reducer would, with no dependency on a
reviewer noticing a hex code in a diff.

## Rejected alternatives

- **Tailwind.** Faster to write, but it moves styling decisions into markup and makes
  `packages/ui` a thin wrapper over utility classes rather than a token layer we designed —
  a weaker artefact for a slice whose point is demonstrating the design-system category. It
  also adds a config surface and a build step that hand-authored CSS Modules do not need.
- **A component library (MUI, Chakra, Radix).** Would ship faster and more polished, but
  the deliverable in this slice is the token architecture, and a component library supplies
  that ready-made — adopting one would make `packages/ui` somebody else's work rather than
  ours.
- **Runtime CSS-in-JS (styled-components, emotion).** Injects `<style>` elements at
  runtime, which fights the phase 6 goal of a Content-Security-Policy with no
  `unsafe-inline`. Paying that cost now to avoid it later isn't a trade worth making.
- **Global BEM CSS.** No scoping, so class-name collisions grow with the app as more
  components are added. CSS Modules give the same authoring model with scoping for free.

## Consequences

A light theme is a redefinition of the semantic block in `tokens.css` — new values for
`--mp-surface-base`, `--mp-text-primary`, and the rest — not a rewrite of any component,
because components never reference a primitive directly.

Every new component owes a contrast check. `tokens.test.ts` computes WCAG contrast ratios
by parsing `tokens.css`, resolving each semantic token's `var()` chain down to a literal
hex value, and checking it against the relevant AA threshold (4.5:1 for body text, 3:1 for
UI boundaries like the focus ring) for every foreground/background pairing currently in use.
Adding a token pairing that isn't in that list gets no contrast coverage; the test is the
enforcement point, not a design review, so a new component's colour choices should extend
the pairing list rather than trust that someone will eyeball it.

**No visual-regression testing exists.** Storybook was deferred — it is a heavy dependency,
a config surface, and a CI job, added in a slice whose stated purpose was that the app
looked bad, and primitives are already covered by Vitest + RTL regardless of whether
Storybook exists. Without Storybook there is nowhere to hang a visual snapshot, so nothing
in this system verifies that a component *looks* correct — only that its tokens resolve to
values with sufficient contrast and that no component leaked a primitive. Appearance is
reviewed by eye. This is a real gap, not a covered one: a change that breaks layout,
spacing, or visual hierarchy while leaving contrast and token usage correct would pass every
automated check in this repository.
