# ADR-010: Real market-data feed — Yahoo, keyless, no fallback

**Status:** Accepted · **Date:** 2026-08-05

## Context

Every live number in the product was, before this slice, invented: `FakeTickService` walks
seeded prices at random, and the price board, alert evaluation, and unrealised P&L all
demonstrated real machinery against fake data. The README carried the debt honestly —
"Polly retry + circuit breaker around the external market data feed (slice 6, not yet
built)" — and the roadmap named the payoff: a real feed makes alerts real rather than a
demonstration against a random walk.

The channel seam slice 1 built made this a second-writer problem, not a rewrite:
`FakeTickService` writes `PriceTick`s into `PriceTickChannel`; `TickBroadcaster` fans out
over `ITickSink`; the worker and the dashboard neither know nor care where a tick came from.
This ADR is the decision that seam let stay narrow — which feed, how it fails, and which of
those failures the system tolerates rather than hides.

The endpoint choice changed mid-design. The spec first named Yahoo Finance's batched
`v7/finance/quote` endpoint — one request for every seeded symbol. Implementing it revealed
that endpoint has required a cookie-and-crumb handshake since 2023: an unversioned,
undocumented anti-scraping mechanism that would have made the client itself the fragile
part of a slice about resilience. That discovery is recorded here, not quietly absorbed,
because it is a real example of the risk decision 1 below accepts on purpose.

## Decision

1. **Yahoo Finance's public chart endpoint, keyless.** `GET
   /v8/finance/chart/{symbol}?interval=1d&range=1d` against
   `https://query1.finance.yahoo.com`, read for `chart.result[0].meta.regularMarketPrice`.
   No API key, no account, ~20-minute-delayed ASX data. The unofficial, unversioned nature
   of this surface is not a weakness to hide; it is this ADR's premise — the resilience
   pipeline (decision 5) and the tolerated-failure-modes table below exist because the
   upstream owes the caller nothing.

2. **Per-symbol requests, not the batched quote endpoint.** The batched `v7/finance/quote`
   endpoint would have cost one request per poll instead of one per symbol, but it has
   required a cookie-and-crumb handshake since 2023 to defeat scraping. Implementing that
   handshake against an API with no published contract for it is exactly the fragility this
   slice exists to refuse — the client would be reverse-engineering an anti-bot mechanism
   that can change without notice, and every retry/breaker test below would be pinned
   against undocumented behaviour instead of documented behaviour. The keyless per-symbol
   `v8/finance/chart/{symbol}` surface has no such gate. The cost is one HTTP request per
   symbol per poll instead of one for the whole batch; decision 6's arithmetic shows that
   cost is still modest.

3. **`MarketData:Source = Fake | Yahoo`, bound and validated, `Fake` the default.**
   `AddInfrastructure` hosts exactly one of `FakeTickService` / `YahooPriceFeedService`,
   chosen by configuration read before options validation runs (so the switch itself never
   depends on validation having completed), with a bad value failing startup via
   `MarketDataOptions.Validate` rather than falling back to a default silently. `Fake` stays
   the default so CI, the e2e suite, and offline development never depend on a third party.

4. **No fallback to the fake when the real feed fails.** If Yahoo is unreachable, rate
   limited, or the breaker is open, `YahooPriceFeedService` writes nothing for the affected
   symbol or poll — it does not substitute a fake tick to keep the board looking alive. This
   is a product decision, not an oversight: a dashboard that silently switches to invented
   prices during a real outage would keep looking live while showing numbers a user might
   trade on, which is worse than looking stale. The dashboard's existing staleness
   surface — `isStale`, em-dashes, the reconnecting pattern already built for the SignalR
   path — is the designed place for "the data stopped." Nothing new was built there for this
   slice because nothing new was needed.

5. **A declared resilience pipeline: retry, then circuit breaker, then per-attempt
   timeout.** `MarketDataResilience.Configure` builds a `Microsoft.Extensions.Http.Resilience`
   pipeline on the typed `YahooQuoteClient`, in that order — retry outermost because a retry
   may span a circuit that broke and recovered mid-poll; breaker inside it; per-attempt
   timeout innermost so one hung request can never stack polls. Retry: 3 attempts,
   exponential backoff with jitter, on `HttpRequestException`, `TimeoutRejectedException`,
   408, 429, and 5xx. Circuit breaker: `FailureRatio` 0.9 over `MinimumThroughput` 6 within a
   90-second `SamplingDuration` — tuned, per the pipeline's own code comment, so that roughly
   two consecutive, fully-retried failed polls open it, not one bad batch — `BreakDuration`
   2 minutes, half-open probe after. Per-attempt timeout: `MarketDataOptions.AttemptTimeout`,
   default 5 seconds, validated to stay shorter than the poll interval. State transitions log
   once each (`OnOpened` at Warning, `OnClosed` at Information); the per-symbol path that hits
   an already-open breaker logs at Debug, so 25 symbols failing the same open circuit in one
   poll produce one warning, not 25.

6. **60-second polling, per-symbol concurrent within a poll.** `YahooPriceFeedService` fans
   one request per seed symbol out concurrently every `PollInterval` (default 60s, no
   throttling machinery — the fan-out is bounded by the symbol count on its own). The 25
   seeded reference tickers (`SeedData.ReferenceTickers`) at one request each every 60
   seconds is 1 poll a minute × 25 symbols = **25 requests a minute** against a keyless
   public endpoint — a request rate an ordinary browser session against the same site would
   produce without particular effort, and nothing on a feed that is already ~20 minutes
   delayed; polling faster would buy no freshness. (An earlier design pass estimated this
   against a smaller assumed symbol count [~8] at a 20s cadence, which would have been 75
   requests a minute against the actual 25-symbol seed set — triple the intended
   politeness. Caught during implementation fact-checking and corrected by moving the
   cadence to 60s rather than trimming the seed set; see the spec's "Amended during
   implementation" note.)

7. **The in-memory rule cache stays deferred.** ADR-009 named a per-tick rule query
   (`AlertRule`s re-queried on every tick, four times a second against the fake) as the
   first thing to break under a real feed, with an in-memory cache invalidated on write as
   the named remedy. The real feed polls at 60s intervals — *fewer* ticks reach the alerts
   worker than the 1-second fake ever produced, so the load this slice was expected to add
   pressure to instead got lighter. Building the cache now would resolve a pressure
   measurement says does not exist; it stays deferred, re-evaluated at slice 7 (history) or
   sooner if cadence ever drops below seconds.

## Tolerated failure modes

The upstream is an unofficial API with no uptime contract. This table is the explicit
statement of which of its failure modes this slice tolerates, and how — the alternative to
each row is silence, which is what this ADR refuses.

| Failure mode | Behaviour |
|---|---|
| **Per-symbol bad quote** — missing, malformed, or non-positive price in an otherwise-200 response | `YahooQuoteClient.GetPriceAsync` returns `null`; the service skips that one symbol's tick with one `LogWarning`, and the other symbols in the same poll are unaffected |
| **Transient failure** — a single 5xx, 408, timeout, or connection failure on one attempt | Retried inside the same poll by the pipeline's retry strategy (3 attempts, exponential backoff with jitter) before the caller ever sees a failure |
| **Sustained failure** — enough consecutive failures across recent polls to trip `FailureRatio` 0.9 over `MinimumThroughput` 6 in the 90s sampling window | The breaker opens; every request for the next 2 minutes fails fast with `BrokenCircuitException` (logged once at Warning on the transition, then Debug per skipped symbol) — no wasted attempts against a downed upstream; a half-open probe after the break duration decides whether to close again; the dashboard's staleness UI is what actually surfaces this to a user, because no tick has been written |
| **Upstream shape change** — a 200 response whose JSON no longer matches the DTOs (renamed field, restructured envelope, Yahoo changing its unofficial contract) | `JsonException` during deserialisation is caught and treated identically to a missing price: `GetPriceAsync` returns `null`, the service skips the symbol with one warning. A silent shape change degrades to the same lossy-skip path as a bad quote, not a crash |
| **Rate limiting (429)** | Classified as transient by the same `IsTransient` predicate as a 5xx; retried with the same exponential backoff before counting toward the breaker's failure ratio. Sustained 429s look like sustained failure and open the breaker the same way |

## Rationale

**Yahoo, keyless, over a keyed or paid provider.** Keyed free tiers (Finnhub, Twelve Data)
are official and documented, which is a real advantage, but their ASX coverage is patchy
and they add secrets management for a portfolio-project feed that gains nothing from being
authenticated. Paid real-time ASX data is out of all proportion to what this project needs
to demonstrate. Choosing the unofficial, keyless surface is what makes the resilience layer
the point of the slice rather than a formality wrapped around a provider that already
guarantees uptime.

**Per-symbol v8 over batched v7.** The batch endpoint's cookie-and-crumb requirement is not
a documented API contract — it is Yahoo's anti-scraping posture, subject to change without
notice and without a changelog. Building a client against it would mean the resilience
pipeline this ADR exists to demonstrate is retrying failures in a handshake nobody at Yahoo
promised would keep working, which is a worse foundation than the one-request-per-symbol
cost decision 2 accepts instead.

**No fallback to the fake.** The alternative — quietly substituting `FakeTickService`'s
random walk when Yahoo is down — was rejected because it is the one failure mode strictly
worse than an honest outage: the UI keeps looking alive while showing numbers that are no
longer connected to reality, and a user watching a watchlist has no way to tell invented
data from real data unless the system tells them. Staleness is a visible, honest failure.
Invented continuity is an invisible, dishonest one. The staleness UI already existed before
this slice (built for SignalR reconnects); reusing it rather than building a second
mechanism is also the cheaper answer, but honesty was the deciding reason, not cost.

**Declared resilience extensions over hand-rolled Polly.** ADR-009 chose the raw
`RabbitMQ.Client` over MassTransit because the messaging topology was the thing that slice
existed to demonstrate. The equivalent instinct here would suggest hand-rolling retry and
breaker logic around a bare `HttpClient` — but `Microsoft.Extensions.Http.Resilience` is
Polly v8 under the hood, declared once at client registration where this ADR can point at
it, and the README's claim was never "the retry loop was authored by hand," it was "Polly
retry + circuit breaker." The extensions deliver exactly that claim, tested against a fake
clock rather than reimplemented.

**Retry → breaker → timeout ordering.** Retry outermost means a request that spans a
breaker recovering mid-attempt still gets its remaining retries. Breaker inside retry means
a tripped breaker short-circuits every remaining retry attempt for free rather than paying
for retries against a circuit that has already decided to fail fast. Timeout innermost means
no single attempt — retried or not — can hang past `AttemptTimeout`, which is what keeps a
hung upstream from stacking polls: without it, a poll whose HTTP call never returns would
still be running when the next `PeriodicTimer` tick fires.

**60-second polling.** The feed itself is ~20 minutes delayed; polling every second would
buy no freshness a human could perceive and would only raise the request rate against a
keyless endpoint for no benefit. 60 seconds keeps the request volume (decision 6) to a
genuinely polite 25 requests/minute against the actual 25-symbol seed set, while producing
a tick cadence still fast enough that the dashboard and the alerts worker continue to
behave as designed. The dashboard's staleness threshold does not get to stay implicit at
this cadence, though — see the consequence below; `STALE_AFTER_MS` was retuned alongside
this decision rather than left pointing at the fake's 1-second rhythm.

## Rejected alternatives

- **Keyed free-tier providers (Finnhub, Twelve Data)** (feed selection). Rejected: patchy
  ASX coverage, secrets management for a key that buys no real benefit here, and quota
  ceilings that would constrain cadence more than the unofficial surface does. See decision
  1.
- **Paid real-time ASX data** (feed selection). Rejected: disproportionate cost and
  operational weight for a portfolio project; the resilience story does not need real-time
  data to be worth telling. See decision 1.
- **The batched, crumb-gated `v7/finance/quote` endpoint** (endpoint shape). Rejected after
  implementation revealed the cookie-and-crumb handshake — reverse-engineering an
  undocumented anti-scraping mechanism is the exact fragility this slice exists to refuse.
  See decision 2 and Context.
- **Auto-detection — try Yahoo, fall back to the fake on failure** (source selection).
  Rejected as the dishonest failure mode (decision 4) in disguise: it would substitute
  invented prices for real ones under exactly the condition — upstream failure — where that
  substitution is least acceptable, just triggered automatically instead of by
  configuration.
- **Silent fallback to the fake on any Yahoo failure** (failure handling). Rejected: see
  decision 4 and its rationale above. The dashboard's honest-staleness surface is the
  designed answer to "the data stopped," and it already existed.
- **Hand-rolled Polly policies around a static `HttpClient`** (resilience mechanism).
  Rejected: `Microsoft.Extensions.Http.Resilience` already delivers exactly the claim the
  README makes — "Polly retry + circuit breaker" — declared once and testable in isolation;
  reimplementing it demonstrates authoring a library's internals, not the failure-mode
  decisions this ADR exists to record. See decision 5.
- **Sub-second or single-digit-second polling** (cadence). Rejected: the feed is already
  ~20 minutes delayed, so faster polling cannot buy freshness a user could perceive, and it
  would raise the request rate against a keyless endpoint for no corresponding benefit. See
  decision 6.
- **Building the in-memory rule cache now** (ADR-009's named remedy). Rejected: the real
  feed's cadence produces fewer ticks than the fake it replaces, so the query pressure the
  cache would relieve did not increase — it decreased. Building it now would be solving a
  problem measurement says does not exist. See decision 7.

## Consequences

**A real outage is now visible on the dashboard, not papered over.** Before this slice,
"the feed is down" had no meaning — the fake never fails. Now it does, and the honest
consequence is that a sustained Yahoo outage or a tripped breaker shows up to every user as
stale prices, exactly as ADR-009's own SignalR-reconnect staleness treatment already
handles. This is the intended behaviour, not a gap.

**The request-rate arithmetic is tied to the seed ticker count, not a fixed assumption.**
`YahooPriceFeedService` polls every symbol in `SeedData.ReferenceTickers` — 25 today — so
the 25-requests-a-minute figure in decision 6 moves if that list grows or shrinks. Nothing
in the pipeline enforces a ceiling on that count; a much larger seed set would eventually
need either a longer poll interval, a per-poll batch limit, or revisiting the endpoint
choice this ADR made.

**The dashboard's staleness threshold is coupled to `PollInterval`, not independent of
it.** `STALE_AFTER_MS` (`apps/dashboard/src/features/prices/streamReducer.ts`) means
"later than the slowest expected source cadence would explain." It was `10_000`ms, a value
tuned for `FakeTickService`'s 1-second walk and never revisited when this slice introduced
a real, much slower cadence; at any `PollInterval` above 10s it would have marked
on-schedule live prices stale between polls, on a schedule, by design of the mismatch — not
a bug in the reducer, a stale constant. It is now `180_000`ms (3 minutes, 3x the 60s
default `PollInterval`), retuned alongside decision 6 rather than left as a silent
assumption. This is a real, if narrow, coupling this ADR did not originally name: a future
change to `PollInterval` should re-examine `STALE_AFTER_MS` rather than assume the
dashboard's staleness UI is cadence-agnostic just because "dashboard changes of any kind"
was out of this slice's scope for everything else.

**The circuit breaker's tuning is calibrated in polls, not raw requests.**
`MinimumThroughput` 6 over a 90-second `SamplingDuration` is tuned (per
`MarketDataResilience.Configure`'s own comment) so that roughly two consecutive,
fully-retried failed polls open the breaker, not one bad batch — deliberately slow enough
that a single blip (one bad poll immediately followed by a good one) never trips it. Because
every seed symbol's request shares the one breaker instance on the typed client, and a poll
fires all of them concurrently, a poll where the upstream is entirely down drives many
failed, fully-retried executions through that shared breaker at once — so in practice a
total outage opens the breaker within the first poll or two, faster than the "roughly two
polls" figure suggests for a single symbol failing in isolation. The cost of the breaker's
patience is a slower reaction the rare time only a handful of symbols are failing rather than
the whole feed.

**Two verification layers, two different guarantees.** `MarketDataResilienceTests` proves
the pipeline's behaviour — retry, open, fail-fast, half-open recovery, timeout cut — against
a `FakeTimeProvider`, with no network and no wall-clock waiting. `MarketDataCompositionTests`
proves the composition switch — exactly one hosted producer per `MarketData:Source` value,
startup validation rejecting a bad one — against a real built host, still with no network
call to Yahoo. Neither test, nor any other automated suite, calls the real Yahoo endpoint;
see `docs/TESTING.md`. The manual rehearsal against the live upstream — watching real prices
tick, setting an alert above a live price, killing the network and watching the breaker open
and the dashboard go honestly stale — is done criterion 2 of the slice's spec and is
recorded separately once it has actually been run.
