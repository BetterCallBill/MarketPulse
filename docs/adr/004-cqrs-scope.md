# ADR-004: CQRS scope

**Status:** Accepted · **Date:** 2026-08-05

## Context

The README has claimed a CQRS architecture — "CQRS via MediatR for Portfolio commands/queries"
— since before a `Portfolio` type existed to make the claim concrete, and `docs/ROADMAP.md`'s
deferred-claims register has carried `docs/adr/004-cqrs-scope.md` as a dead link since slice 1:
the document the README cites did not exist. Slice 5a is where the question stops being
abstract. Building the `Portfolio` aggregate (`docs/superpowers/specs/2026-08-05-portfolio-backend-design.md`)
forces an actual answer to "how much CQRS" — a full split with a dedicated read store and
projections, or MediatR's command/query separation over the one database this system already
has — and this ADR is that answer, written against real code rather than a placeholder.

A second, narrower question rides along with the first. `Portfolio` reintroduces the exact
optimistic-concurrency shape `AlertRule` established (ADR-009, decision 6): a `RowVersion`
token, checked on the row's `UPDATE`, that turns a lost race into a 409 instead of a silent
overwrite. Making that token actually guard every trade — not just the ones that happen to
touch the portfolio row directly — turned out not to be free, and the mechanism that makes it
work is as much a part of this slice's honesty as the CQRS scoping is. Both belong in one ADR:
the concurrency posture is the thing the isolation-level line of the README's coverage map
("serializable vs read-committed demonstrated...") is actually about, and that claim is a CQRS
adjacent-but-separate demonstration this ADR is also the right place to settle.

## Decision

1. **CQRS scope is MediatR commands/queries over one store, nothing more.** Writes go through
   the `Portfolio` aggregate — `RecordTransactionCommand` calls `RecordBuy`/`RecordSell`, which
   enforce the average-cost re-averaging and the oversell invariant before anything is
   persisted. Reads that would force the aggregate to hold unbounded state go straight to the
   table instead: `GetTransactionsQuery` pages the `Transactions` table directly rather than
   materialising the aggregate to get at a list it deliberately does not hold (see `Portfolio`'s
   doc comment — the aggregate mints transactions via an internal constructor but does not keep
   them). `GetPortfolioQuery` reads the aggregate's own state (holdings, realised P&L) because
   that state *is* bounded — one row per ticker the user has ever held. No separate read
   store, no projections, no event sourcing. The transaction table already **is** the append-only
   log of everything that happened to a portfolio; nothing here needs to replay it into a second
   representation to answer the questions this API is asked.

2. **Writes carry a monotonic `Portfolio.Version` counter precisely so the aggregate's
   `RowVersion` check guards every trade.** `RecordBuy`/`RecordSell` mutate a `Holding` — a
   child entity mapped to its own table via EF's owned-collection mapping — and nothing about
   that write touches a column on the `Portfolios` row on its own. `RowVersion`, the actual
   concurrency token, is only ever checked on an `UPDATE` issued against the row it lives on;
   a trade that never generates that `UPDATE` is a trade `RowVersion` never protects. `Version`
   is a plain, unindexed `bigint`, incremented in `RecordBuy`/`RecordSell` after validation,
   entirely in the Domain. Because it changes on every single trade, EF's snapshot-based change
   tracking can never see it as unchanged, so a `Portfolios` `UPDATE` — carrying the `RowVersion`
   check — is issued on every trade without exception. `LastTradedUtc` records the same event for
   display and is explicitly not load-bearing for this; see its doc comment on `Portfolio`.

## Rationale

**One store vs. a dedicated read model.** A read store earns its cost when reads and writes
have genuinely divergent shapes, or when read load needs to scale independently of write load.
Neither is true here: `GetPortfolioQuery`'s shape (holdings + total realised P&L) is a
straightforward projection of the aggregate MediatR already builds for writes, and this
system's read volume is one user looking at their own small portfolio, not a fan-out problem.
Building a second store to answer a question the first store already answers efficiently would
have been complexity spent on a scaling problem this product does not have.

**The concurrency mechanism was not the first thing tried.** An earlier version forced the
guarantee from `MarketPulseDbContext`: a `SaveChanges`/`SaveChangesAsync` override that
detected any `Portfolio` with a modified `Holding` in the change tracker and force-set that
`Portfolio` entry to `EntityState.Modified` before saving. It worked, and it was rejected in
review on three grounds. It was **infrastructure-hidden** — the guarantee that made
`RowVersion` load-bearing lived in a `DbContext` override the Domain type's own doc comment
did not (and could not honestly) describe, so reading `Portfolio.cs` alone gave a wrong
picture of what actually protected a trade. It was **type-hardcoded** — the override checked
specifically for modified `Holding` entries, so any future owned collection added to
`Portfolio` would silently fall outside it and reintroduce the exact unguarded-write gap this
mechanism exists to close, with no compiler or test to catch the omission. And it caused
**full-row `UPDATE`s** — force-setting `EntityState.Modified` marks every scalar property
modified regardless of whether its value changed, so the generated `UPDATE` rewrote every
column on `Portfolios`, `UserId` included, on every single trade. `Portfolio.Version` fixes
all three at once: the guarantee is a Domain-level counter with a doc comment that states its
purpose plainly and warns any future Holding-mutating method to bump it too; it depends on no
knowledge of which child collections exist; and because only `Version` (and `LastTradedUtc`)
actually change, the generated `UPDATE` touches only the columns that changed — confirmed
against the SQL log during implementation.

**Isolation level: token over transaction, at the aggregate that actually races.** The
README's original claim — "serializable vs read-committed demonstrated in the mock order
placement flow" — described running production writes inside a serializable transaction.
That would demonstrate isolation levels literally, but it imports deadlock detection and retry
handling that nothing else in this codebase has, and it diverges from the precedent
`AlertRule` already set: `RowVersion` optimistic concurrency, decided in ADR-009 for exactly
the same reason (letting racers proceed and paying the cost only on the rare actual collision,
rather than serializing every write to guard against a collision that usually will not
happen). The spec's decision table records this choice explicitly and this ADR carries it
forward: the demonstration the README promises does not disappear, it moves into
`PortfolioConcurrencyAnomalyTests` — see Consequences.

## Rejected alternatives

- **Full CQRS with a dedicated read store** (query-side projections, materialised views, or a
  second database). Rejected: nothing in this system has read/write shapes or read/write scale
  divergent enough to earn the synchronisation problem a second store creates. See decision 1.
- **Event-sourced portfolio** (rebuild aggregate state by replaying a `TransactionRecorded`
  event stream). Rejected: the `Transactions` table already *is* the event log in every way
  that matters here — append-only, ordered, a faithful record of everything that happened —
  without needing the replay machinery, snapshotting, or upcasting that a real event-sourced
  implementation would add for no additional capability this product needs.
- **Handler-per-file-per-layer ceremony beyond what the codebase already does** (separate
  command/query interfaces, dedicated result-wrapper types, a distinct pipeline per slice).
  Rejected: the existing MediatR + FluentValidation pipeline (`ValidationBehaviour<,>`) already
  gives commands and queries their separation and their validation; adding another layer of
  indirection on top would be structure without a problem it solves.
- **A `SaveChanges` override forcing modified `Portfolio` rows into concurrency checks**
  (the mechanism that predated `Portfolio.Version`). Rejected in review: infrastructure-hidden,
  hardcoded to the `Holding` type, and it rewrote every scalar column on every trade. See
  decision 2 and the Rationale above.

## Consequences

**Corrections are append-only, not edits.** `Transaction` has no removal and the API has no
delete or amend endpoint (`docs/superpowers/specs/2026-08-05-portfolio-backend-design.md`'s
scope boundary names this explicitly). An incorrect trade is corrected by recording an
offsetting one. This is the direct cost of "the transaction table is the event log" — an
event log is not edited, it is appended to.

**Replayed idempotent responses are not a perfect replay.** `IdempotencyFilter` stores and
replays a response's status code and JSON body; it does not store per-response headers, so a
replayed `201` from `POST /portfolio/transactions` carries no `Location` header on the second
delivery, and the replayed body is served as `application/json` rather than the
charset-qualified content type ASP.NET Core would have written on the original response. A
client that depends on either of those on a replay will not get them.

**Idempotency keys live forever, and a crash mid-claim orphans one.** `IdempotencyKeys` rows
are never expired or purged in this slice — no retention job exists. A claim is inserted before
the action runs and completed (or removed, on failure) after; a hard crash of the process in
that narrow window leaves a claimed-but-never-completed row with no remedy but a fresh key,
since nothing reclaims a stale in-flight claim either. Both retention and stale-claim reclaim
are named here as one deferred operations concern, not built speculatively against a failure
mode that has not yet cost anything.

**The isolation-level demonstration lives in tests, not in production transaction options.**
No portfolio write runs at `Serializable`; every request runs at SQL Server's default,
read-committed, exactly like the rest of this codebase. The demonstration the README's
coverage map promises is `PortfolioConcurrencyAnomalyTests`: test (a) reproduces the
read-committed lost-update anomaly by giving a second racing write the first's committed
`RowVersion` — the one thing standing between "two racing sells" and "a trade a correct,
non-racing validation would have rejected is instead accepted, silently erasing the other
trade's effect" — and proves the loss by replaying the loser's exact trade against the real
post-race state, where it is honestly rejected. Test (b) removes the bypass and shows the same
race producing a `DbUpdateConcurrencyException`, which `ExceptionHandlingMiddleware` maps to
409 `concurrent-update`. This is the RowVersion-over-serializable choice from the spec's
decision table, made executable: the anomaly is real and demonstrated, the defence is real and
demonstrated, and the cost of the defence — a caught exception on the rare actual collision —
is paid only where two writes actually race, not on every write in the system.
