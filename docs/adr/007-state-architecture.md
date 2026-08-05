# ADR-007: Client-state architecture

**Status:** Accepted · **Date:** 2026-08-05

## Context

The README's original architecture description promised a "two-layer" client: server state
in TanStack Query, client/UI state in Zustand. That claim was written before any client state
existed to test it against — slice 1 explicitly excluded Zustand as having "no genuine
client-owned state yet" — and the roadmap carried it forward as a half-true dependency the
codebase did not actually have (`docs/ROADMAP.md`'s deferred-claims register, and its warning
that "Zustand should not be added merely to satisfy ADR-007"). Slice 4b was named as the
likely trigger: it adds the one plausible candidate for a second state layer, unread
notification state, on top of the alerts pipeline slice 4a already built. This ADR is the
decision that trigger forced — whether the second layer gets built, or whether the document
is wrong. See `docs/superpowers/specs/2026-08-05-alerts-ui-chaos-test-design.md`, which frames
this as "the client-state question the roadmap has carried since slice 3."

## Decision

**Client state is the server cache.** TanStack Query holds every piece of server-owned truth
the dashboard displays — watchlist rows, alert rules, notifications — and nothing server-owned
is duplicated into a second store.

1. **SignalR pushes are optimisations patched into that cache, not a parallel source of
   truth.** `usePriceStream` and `useNotificationStream` both parse an inbound payload and
   write it into the existing TanStack Query cache (`useNotificationStream` prepends the
   pushed notification into the `['notifications']` query via `setQueryData`, and invalidates
   `['alerts']` because a notification implies some rule just flipped to `Triggered`). A
   reconnect invalidates and refetches rather than trusting the gap to have been silent — the
   push is the optimisation, the row is the guarantee, and a client-side cache that could
   diverge from the server's answer would be a second copy of the same bug class ADR-009
   already rejected once for the message broker.
2. **Derived values are computed at render, never stored.** The unread badge is
   `notifications.filter(n => !n.isRead).length`, read straight off the `['notifications']`
   cache on every render. Storing that count anywhere — a Zustand slice, a `useState`, a
   `useMemo` even — would be a cache of a cache: a second value that the mutation, the push,
   and the reconnect path would all have to remember to keep in sync, for a computation cheap
   enough to redo every render.
3. **Ephemeral view state is `useState` and beneath architecture.** Whether the notifications
   panel is open is not a fact about the server, not shared between components, and gone the
   moment the component unmounts — it is exactly the state React's own primitives exist for,
   and giving it a store would be reaching for infrastructure a local variable already covers.

No client-side state library is a dependency of this codebase.

## Rejected: Zustand

The README promised a "TanStack Query + Zustand" two-layer client. The only candidate for the
second layer, unread notification state, turned out to be server state in disguise: the server
already owns `IsRead` per notification row and exposes the mutation
(`POST /api/v1/notifications/{id}/read`). Adding Zustand to hold a client-side "unread" flag
would have created a second copy of data the server already tracks, with an invalidation
problem neither copy needed on its own — the push updates the cache, the mutation updates the
cache, and a Zustand slice sitting beside both would need its own logic to stay in agreement
with a value TanStack Query already had. The only thing that construction would have
accomplished is making the codebase match a document. The document moved instead.

## Trigger to revisit

Genuinely client-owned cross-component state that no server row backs — state where "what is
the server's value for this" is not a coherent question. Concretely: a multi-step form wizard
holding in-progress, unsubmitted field values across steps that no endpoint has seen yet;
optimistic UI that goes further than a boolean flip on a row that already exists (e.g.
client-computed derived data shared across components before any request completes); or
cross-tab UI coordination that has no server concept (a client-side "recently dismissed"
list, say, if the server never learns about a dismissal). Slice 5's portfolio flows or a
future multi-step onboarding wizard are the concrete candidates to test this against — if one
of them needs state like that, this is where Zustand (or an equivalent) earns its place, and
this ADR should be revised rather than quietly bypassed. Until then, per the roadmap's warning,
it is not added merely to satisfy a document.

## Consequences

**The README's two-layer claim is corrected, not merely footnoted.** Every place the README
and its Pro variant described a "TanStack Query + Zustand" split now describes "TanStack Query
as the single client-side state layer; live data patched into the query cache over SignalR
(ADR-007)." `docs/ROADMAP.md`'s deferred-claims register row for this ADR now points at this
document instead of describing a half-true dependency.

**A reconnect must invalidate, not just resume.** Because the cache is the only copy, any gap
in the SignalR connection is a gap in what the cache can be trusted to reflect — the reconnect
handler's obligation to invalidate (`['notifications']` on the notification stream, alongside
the existing price-stream behaviour) is now load-bearing in a way it would not be if a second
store existed to fall back on.

**Every future feature that looks like it needs client state is a design question, not a
default.** The candidates named above are the test: if a feature's state is a server row with
a lag, it belongs in TanStack Query and a mutation; only state with no server row to be a lag
*of* is a genuine candidate for something else.

## Addendum: 2026-08-05 — slice 5b, unrealised P&L

Slice 5b is the third worked example, and the one this ADR's "trigger to revisit" section
named by name: the portfolio dashboard's per-holding unrealised P&L. It is computed
`units × (livePrice − averageCost)` in `HoldingsTable`, from two sources already in the
cache — the `['portfolio']` query (server truth: units, average cost, realised P&L) and
`usePriceStream`'s public broadcast (the same stream the watchlist already reads, no new
subscription) — joined **at render**, on every render, and stored nowhere. A holding whose
ticker has not ticked yet renders an em-dash rather than a zero, because zero would claim
knowledge of a value that does not exist; the footer total follows the identical rule and
turns into an em-dash the moment any held ticker lacks a price, because a partial sum over
unknown terms is not a number, it's a lie with decimal places.

This is the same shape as the unread-badge example above, not a new one: a value cheap to
recompute every render, derived from state that already lives in the query cache and a
stream, with no mutation, no reconnect handler, and no component that needs it to persist
past its own render. It was also the concrete candidate this ADR asked to be tested against
before Zustand earned a place — "a live price joined against a server row" is exactly the
shape of question this document already answers, and it answers it the same way: no second
state layer. Trade-form submission state (the pending idempotency key) is `useRef`, not a
store, for the same reason the notification panel's open/closed flag is `useState` — it is
scoped to one component's one interaction, gone the moment that interaction resolves, and
never read by anything else.

No client-side state library is a dependency of this codebase, still.
