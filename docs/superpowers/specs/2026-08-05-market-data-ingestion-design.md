# Slice 6 — Real market-data ingestion: "Ticks that owe us nothing"

**Date:** 2026-08-05
**Depends on:** slice 1 (the tick channel and fan-out), slice 4a (alerts consume ticks). Everything downstream is source-agnostic by construction.
**Branch:** `feature/slice-6-market-data-ingestion` → `test`

---

## Why this slice exists

Every live number in the product is currently invented. `FakeTickService` walks seeded
prices at random, and everything the last five slices built on top of it — the price board,
alert evaluation, unrealised P&L — demonstrates real machinery against fake data. The README
promises "Polly retry + circuit breaker around the external market data feed (slice 6, not
yet built)"; the roadmap phrases the payoff precisely: *a real feed makes alerts real rather
than a demonstration against a random walk.*

The architectural seam already exists and this slice deliberately does not move it:
`FakeTickService` writes `PriceTick`s into `PriceTickChannel`; `TickBroadcaster` fans out
over `ITickSink` to SignalR and RabbitMQ; the worker and the dashboard neither know nor care
where ticks come from. The real feed is a second writer behind the same channel, chosen by
configuration. The slice's genuine new content is the **resilience layer and the honesty
about failure**: the upstream is an unofficial API that owes us nothing, and ADR-010 exists
to say exactly which of its failure modes we tolerate and how.

### Decisions taken, with rejected alternatives

| Decision | Chosen | Rejected because |
|---|---|---|
| Feed | **Yahoo Finance's public quote endpoint** (batched multi-symbol, ~20-min-delayed ASX data, no key) | Keyed free tiers (Finnhub, Twelve Data) are official but patchy on ASX coverage, add secrets management, and their quotas constrain cadence. Paid real-time ASX data is out of all proportion to a portfolio project. The unofficial upstream is not a weakness to hide but the ADR's premise: the resilience layer exists precisely because the API is unversioned and unowed |
| Source selection | **`MarketData:Source = Fake \| Yahoo`, bound options, `Fake` the default** | Replacing the fake outright breaks e2e determinism (the journeys assert on prices flowing within seconds) and offline development. Auto-detection (try Yahoo, fall back to fake) was rejected as the dishonest failure mode below in disguise |
| Feed failure at runtime | **Prices go stale, truthfully. No fallback to the fake** | Silently substituting a random walk for real prices when the upstream dies is the one unacceptable failure mode: the UI would keep looking alive while showing invented numbers a user might trade on. The staleness treatment the dashboard already has (`isStale`, em-dashes, the reconnecting pattern) is the designed surface for "the data stopped"; ADR-010 records this as a product decision, not an accident |
| Cadence | **One batched request for all seeded tickers every 15s (configurable)** | Per-symbol requests multiply calls for nothing (Yahoo's endpoint is multi-symbol). Faster polling buys no freshness on a ~20-min-delayed feed and is impolite to an unofficial API. 15s for ~8 symbols is 4 requests/minute |
| Market closed | **Broadcast every successful poll as a tick, unchanged price, neutral direction** | Suppressing unchanged prices means every ASX evening the board decays to stale and the "live" claim quietly becomes false for 18 hours a day. A repeated last-trade price is real data, honestly timestamped by the poll |
| Resilience mechanism | **`HttpClientFactory` + `Microsoft.Extensions.Http.Resilience` pipeline: retry with jitter, then circuit breaker** | Hand-rolled Polly policies wired around a static `HttpClient` re-implement what the resilience extensions ship tested; the README's claim is "Polly retry + circuit breaker", which this is (the extensions are Polly v8 under the hood), with the pipeline declared beside the client registration where the ADR can point at it |
| In-memory rule cache | **Still deferred, now with better evidence** | ADR-009 names the cache as the remedy "when a real feed breaks" the per-tick rule query. The real feed polls at 15s — *fewer* ticks than the 1s fake, so the load got lighter, not heavier. Building the cache now would be resolving a pressure that measurement says does not exist. Re-evaluate at slice 7 (history) or if cadence ever drops below seconds |

---

## Scope boundary

### In scope

| Layer | Deliverable |
|---|---|
| Infrastructure | `YahooPriceFeedService` (`BackgroundService` in `RealTime/`, beside the fake): batched poll → parse → validate → write `PriceTick`s to `PriceTickChannel`; `YahooQuoteClient` (typed `HttpClient` with the resilience pipeline); symbol mapping (`IVV` ⇄ `IVV.AX`) beside the feed; `MarketDataOptions` (`Source`, `PollInterval`, validated, `ValidateOnStart()`) |
| Composition | `AddInfrastructure` registers the tick source by `MarketData:Source` — exactly one of `FakeTickService` / `YahooPriceFeedService` is hosted. Default `Fake`; `appsettings.json` documents the switch |
| Docs | **ADR-010** (feed selection; the tolerated-failure-modes table; the no-fallback decision); README resilience claims flipped to built; ROADMAP; TESTING.md |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

Tick persistence, history, candles, charts (slice 7) · BenchmarkDotNet, Span parsing, load
tests (slice 12) · the in-memory rule cache (deferred with evidence — see decisions) · new
tickers beyond the seeded set · trading-hours awareness or calendars (the closed-market
behaviour needs none) · API-key management (the chosen feed has no key) · a provider
abstraction beyond what the channel seam already gives (a second provider would slot in as a
third `BackgroundService`; interface extraction waits for the second concrete need) ·
dashboard changes of any kind (staleness UI already exists).

---

## Architecture

### The poller

`YahooPriceFeedService` runs the fake's exact loop shape (`PeriodicTimer`, cancellation via
the stopping token, log-and-continue):

1. Every `PollInterval` (default 15s), request all seed symbols in one call:
   `GET /v7/finance/quote?symbols=IVV.AX,NDQ.AX,…` on the configured base URL.
2. Parse with `System.Text.Json` DTOs. Per symbol: map back to the ASX code, validate the
   price is positive and the symbol is one we asked for; write one `PriceTick` per valid
   quote with `DateTimeOffset.UtcNow` (poll time, not the feed's delayed trade time — the
   tick timestamp means "when we observed it", consistent with the fake).
3. A quote that is missing, malformed, or non-positive is skipped with one warning —
   per-symbol, so one bad quote never suppresses the other seven. A response that is not
   200/parseable counts as a failed poll.
4. A failed poll writes nothing. The next poll self-heals. Ticks are lossy by design —
   4a's words, unchanged.

The upstream endpoint host is configuration (`MarketDataOptions.BaseUrl`), not for
multi-provider dreams but so the unit tests point the real client at a stubbed handler and
the ADR can name the real value in one place.

### The resilience pipeline

Declared on the typed client at registration:

- **Retry:** 3 attempts, exponential backoff with jitter, on transient failures (5xx, 408,
  `HttpRequestException`, timeout). Handles blips inside one poll.
- **Circuit breaker:** opens on sustained failure (sampling window tuned so ~4 consecutive
  failed polls trip it), stays open ~2 minutes, half-open probe. While open, polls fail
  fast — the service logs at warning on state change, once, not per poll.
- **Per-attempt timeout** well under the poll interval, so a hung upstream can never stack
  polls.

Numbers are options-bound with defaults; the ADR carries the reasoning, the tests pin the
behaviour (breaker opens, polls skip, half-open recovers).

### What downstream sees

Nothing new. Ticks arrive on the channel as before; `TickBroadcaster` fans out; SignalR
clients and the alerts worker are byte-for-byte unaware of the source. When the feed is down
or the breaker is open, ticks simply stop and the dashboard's existing staleness UI tells
the truth. This non-event is the design working.

## Testing

| Suite | What |
|---|---|
| xUnit (unit) | Parsing a captured real Yahoo response fixture (checked into the test project); symbol mapping both directions incl. unknown-symbol-in-response ignored; malformed/non-positive quote dropped per-symbol while siblings tick; batch URL construction; timestamp is observation time |
| xUnit (unit, resilience) | Against a scripted `HttpMessageHandler`: transient failure retried within a poll; sustained failure opens the breaker (subsequent polls fail fast, no handler hit); half-open probe recovers; a hung handler is cut by the per-attempt timeout |
| xUnit (integration, composition) | `MarketData:Source=Fake` (and unset) hosts exactly `FakeTickService`; `=Yahoo` hosts exactly `YahooPriceFeedService`; options validation rejects a bad source or non-positive interval at startup — asserted against the built host's services, no network |
| Existing suites | All unchanged and green — the strongest claim this slice can make is that nothing downstream noticed |

### Deliberately not tested

Live calls to Yahoo in any automated suite (CI must not depend on an unofficial third
party's uptime) · trading-hours behaviour (none exists) · the dashboard (unchanged).

---

## Done criteria

1. All existing suites green with zero changes to their code; new unit + composition tests
   green.
2. **The manual rehearsal, this time actually rehearsed** (4a's lesson): run the API with
   `MarketData:Source=Yahoo`, watch real delayed ASX prices tick on the watchlist, set an
   alert above the live price and see it fire through the real pipeline; then kill the
   network and watch prices go honestly stale with the breaker logged open. Evidence (what
   was seen, when) recorded in the report/commit message.
3. e2e suite green on the default Fake source, proving determinism survived.
4. ADR-010 exists; the README's two "slice 6, not yet built" resilience claims now point at
   built code; ROADMAP and TESTING.md updated.
5. No new references from Application/Domain outward; `DependencyRuleTests` green.

---

## Interview category coverage

| # | Category | What this slice banks |
|---|---|---|
| 12 | **Cloud & DevOps / resilience** ⭐ | Retry-with-jitter + circuit breaker + timeout as a declared pipeline, with tests that open and recover the breaker; failure modes chosen and documented rather than discovered |
| 10 | **Architecture & system design** | The channel seam paying off — a real feed lands without touching a consumer; the no-fallback decision as a worked example of honest degradation; the rule cache deferred on evidence |
| 4 | **ASP.NET Core depth** | `HttpClientFactory` typed clients, options validation at startup, hosted-service selection by configuration |

---

## What comes next

**Slice 7** (price history and charts) inherits data worth persisting: tick persistence,
Dapper history queries, candle aggregation, the deliberate N+1 postmortem (ADR-006), and the
dashboard chart. After that the roadmap's phase-5 operational slices (observability, IaC)
put eyes and repeatability on what is now a genuinely live system.
