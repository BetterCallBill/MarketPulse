# ADR-002: Walking skeleton before layer completion

**Status:** Accepted · **Date:** 2026-07-31

## Context

The original delivery plan built the backend completely (weeks 1–6) before any frontend
work. That leaves the highest-risk seam in the system — ingestion → SignalR → React
re-render — unproven until week 3, and produces nothing demoable until week 7.

## Decision

Slice 1 wires one thin feature through every layer: Domain → EF Core → REST → React, plus
a synthetic tick source through `Channel<T>` → SignalR → a live-updating cell. Later
phases thicken existing layers rather than introducing new ones.

## Rationale

Integration risk concentrates at seams, not inside layers. A skeleton pays the integration
cost while the codebase is small enough to change cheaply.

## Rejected alternatives

- **Phase 1 as originally written.** Targets the primary interview surface fastest, but
  defers all seam risk and produces no running system for six weeks.
- **Scaffolding-only first slice.** Green CI over an empty test suite is not evidence that
  anything works.

## Consequences

Some rework is accepted where a stub is later replaced — specifically `DevAuthMiddleware`
and `FakeTickService`. Both are deliberately isolated behind interfaces (`ICurrentUser`)
and seams (`PriceTickChannel`) so the replacement touches one file each.

## Dependency notes

MediatR is pinned to 12.x, the last MIT-licensed major version; 13.x moved to a commercial
licence. FluentValidation, zod, and TanStack Query are each used at more than one call site,
satisfying the "defend every dependency" principle.
