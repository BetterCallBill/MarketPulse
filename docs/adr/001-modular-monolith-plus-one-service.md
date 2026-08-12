# ADR-001: Modular monolith plus one extracted service

**Status:** Accepted · **Date:** 2026-07-31

## Context

MarketPulse needs to demonstrate distributed-systems judgement without the operational
cost of a full microservice estate built by one person.

## Decision

Portfolio and Market Data ship as modules inside one deployable (`MarketPulse.Api`).
Alert evaluation is extracted into a separate worker (`MarketPulse.Alerts`) from Phase 3,
communicating over RabbitMQ.

## Rationale

Alert evaluation is the only component with a genuinely different scaling profile: it is
CPU-bound, bursty on price movement, and tolerant of eventual consistency. Everything else
shares the same request-scoped lifetime and the same database transaction boundary, so
splitting it would buy distributed-transaction problems and no independent scaling.

## Rejected alternatives

- **Pure monolith.** Simpler, but forfeits any demonstration of messaging, idempotent
  consumers, or eventual consistency — category 11 in the interview map.
- **Microservices throughout.** Four or five services would each need their own pipeline,
  database, and observability wiring, for a system with one user. The cost is real and the
  benefit is imaginary at this size.

## Consequences

One RabbitMQ dependency and one extra deployable from Phase 3. The module boundary between
Portfolio and Market Data must stay clean enough that either could be extracted later —
enforced by project references, not convention.
