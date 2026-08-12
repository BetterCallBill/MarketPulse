# Slice 2 — Authentication: "Real users, real sessions"

**Date:** 2026-08-02
**Status:** Approved (design), pending implementation plan
**Previous slice:** [2026-07-31-walking-skeleton-design.md](2026-07-31-walking-skeleton-design.md)
**Parent spec:** [MarketPulse-Pro-README.md](../../MarketPulse-Pro-README.md)
**Category reference:** [intervew-aspects.md](../../intervew-aspects.md)

---

## Why this slice exists

Slice 1 shipped a watchlist that walks and prices that tick, but every request in it is
made by the same hard-coded user. `DevAuthMiddleware` invents a `ClaimsPrincipal` out of
a seeded GUID, and the slice 1 spec named its replacement as the next sub-project:

> Candidate slice 2 is real authentication (JWT + refresh + OIDC), which deletes
> `DevAuthMiddleware`, unblocks Playwright, and banks most of category 7.

This slice does that, minus OIDC (see *Scope boundary*).

The structural point is that slice 1 built the seam on purpose. `CurrentUser` reads
`ClaimTypes.NameIdentifier` off `HttpContext.User` and has no opinion about who put it
there. So slice 2 deletes one middleware, adds real authentication that populates the same
claim, and **changes no repository, no handler, no query, and no watchlist schema**. A
walking skeleton earns its keep at exactly this moment; the ADR should say so.

### Decisions taken, with rejected alternatives

| Decision | Chosen | Rejected because |
|---|---|---|
| Auth scope | Local accounts (email + password) | Adding OIDC in the same slice introduces an external provider, a second identity-linking path in `User`, and E2E tests that cannot run offline. It gets its own slice |
| Token transport | **httpOnly cookies + CSRF** | Bearer-in-JS-memory is the SPA convention but the IETF *OAuth 2.0 for Browser-Based Applications* BCP discourages holding tokens in the browser at all. Cookies are the enterprise-aligned choice and a deliberate step toward BFF. Also what the parent README already commits to (line 145) |
| Identity stack | `PasswordHasher<T>` only, from ASP.NET Core Identity | Full Identity forces `IdentityUser<Guid>` — an Infrastructure type — into the model, which means either a second parallel user model kept in sync with `Domain.User`, or a `Domain` → Identity reference that breaks slice 1's dependency-rule test. Fully hand-rolled hashing (Argon2id) is a better algorithm but means owning crypto parameter tuning to no interview benefit |
| Refresh strategy | Rotation **with reuse detection** | Non-rotating refresh tokens are a long-lived bearer credential. Rotation without reuse detection silently tolerates a stolen token being replayed once. Reuse detection is also the one thing full Identity would not have given us |
| Session identity | JWT inside the cookie | An opaque server-side session would work and is arguably simpler, but banks none of the JWT signing/claims/validation/expiry material the parent README stakes category 7 on |
| Password policy | NIST SP 800-63B | Composition rules ("one uppercase, one symbol") are what NIST now advises *against*, on the evidence they push users toward `Password1!` |
| Authorization | `[Authorize]` only, no policies | MarketPulse has no privileged action yet, so a policy layer would be a `RequireRole("Admin")` guarding nothing, with no admin user and no admin screen. It belongs to the slice that introduces a real privileged action |

---

## Scope boundary

### In scope

| Layer | Deliverable |
|---|---|
| Domain | `User` gains `PasswordHash`, `CreatedUtc`, `FailedLoginCount`, `LockoutEndUtc`; new `RefreshToken` entity with rotation/revocation invariants |
| Application | `RegisterUserCommand`, `LoginCommand`, `RefreshSessionCommand`, `LogoutCommand` handlers; `IPasswordHasher`, `ITokenService`, `IUserRepository`, `IRefreshTokenRepository` abstractions; FluentValidation on register and login |
| Infrastructure | `PasswordHasher<User>` adapter, `JwtTokenService`, EF configuration + migration for the new columns and `RefreshTokens` table, repository implementations |
| Api | `AuthController` (`/api/v1/auth`), cookie issuance, JWT bearer handler reading from the cookie, CSRF double-submit middleware, login rate limiting, `[Authorize]` on `WatchlistController` and `PriceHub`, **`DevAuthMiddleware` deleted**, `JwtOptions` via the options pattern with `ValidateOnStart()` |
| Frontend | Router, `/login` and `/register` screens, `ProtectedRoute`, `useSession()`; `api-client` gains `credentials: 'include'`, the CSRF header, and single-flight 401 → refresh → retry |
| Testing | Unit + integration as tabulated below; **cross-user isolation tests**; **Playwright E2E** |
| CI | New E2E job (docker-compose'd SQL Server + API + built dashboard) |
| Docs | ADR-003 (cookie/BFF vs bearer, with rejected alternatives); `TESTING.md` updated to close the "no E2E" gap slice 1 recorded |

### Out of scope

Named explicitly so their absence is a decision, not an oversight:

OIDC / social login · policy-based authorization · password reset · email confirmation ·
MFA · the full BFF proxy hop · device or IP binding of refresh tokens ·
session-management UI · account deletion · "remember me" · audit log of login history.

---

## Architecture

### What does not change

`ICurrentUser` / `CurrentUser` keep their exact slice 1 implementation. `GetWatchlistQuery`,
`AddWatchlistItemCommand`, `RemoveWatchlistItemCommand`, `WatchlistRepository`,
`MarketPulseDbContext`'s watchlist mapping and both existing migrations are untouched. The
only edit to `WatchlistController` is an `[Authorize]` attribute.

### Domain model

`User` gains four properties. Lockout counters live on `User` rather than in a separate
attempts table — one less table, and forensic login history is out of scope.

`RefreshToken` is its own entity, with the rotation rules enforced on the entity rather
than in the handler:

| Property | Purpose |
|---|---|
| `Id` | Primary key; also the family link target |
| `UserId` | Owning user |
| `TokenHash` | SHA-256 of the token. **The token itself is never stored** — a database dump yields no usable sessions. SHA-256 rather than PBKDF2 is correct here: the token is 256 bits of cryptographic randomness, not a low-entropy human secret, so there is nothing for a slow hash to defend against |
| `ExpiresUtc` | 14 days from issue |
| `CreatedUtc` | Issue time |
| `RevokedUtc` | Null while live |
| `ReplacedByTokenId` | Set on rotation; forms the family chain |

`Domain` keeps zero dependencies. Hashing sits behind `IPasswordHasher` in `Application`,
implemented in `Infrastructure` over `Microsoft.AspNetCore.Identity.PasswordHasher<User>`.
That generic is constrained to `class`, not to `IdentityUser`, so the audited PBKDF2
implementation is reused without any Identity type crossing an architectural boundary.

### API surface

| Endpoint | Behaviour |
|---|---|
| `POST /api/v1/auth/register` | Creates the user and an empty watchlist, then logs in — issues all three cookies |
| `POST /api/v1/auth/login` | Verifies the password hash, issues all three cookies |
| `POST /api/v1/auth/refresh` | Rotates the refresh token, re-issues `mp_access` and `mp_csrf` |
| `POST /api/v1/auth/logout` | Revokes the refresh family, clears all three cookies |
| `GET /api/v1/auth/me` | Returns the current user, or `401`. Backs `useSession()` on the client |

### Token lifecycle

```
POST /api/v1/auth/login
  └─ verify password hash → mint
      ├─ access JWT   15 min   → mp_access   httpOnly, SameSite=Lax,    Secure, Path=/
      ├─ refresh      14 days  → mp_refresh  httpOnly, SameSite=Strict, Secure,
      │                                       Path=/api/v1/auth
      └─ csrf nonce            → mp_csrf     readable by JS (double-submit)
```

Three deliberate details:

**The refresh cookie is path-scoped** to `/api/v1/auth`, so it is not transmitted on
watchlist, hub, or health requests. Free, and it limits the blast radius of any single
request being observed.

*Amended 2026-08-03, during implementation.* This was originally scoped to
`/api/v1/auth/refresh` alone. That is narrower, and wrong: RFC 6265 path-matching then
never sends the cookie to `/api/v1/auth/logout` either, so logout silently revoked nothing
server-side and a token captured beforehand stayed valid for its full 14 days. Scoping to
the `/api/v1/auth` prefix keeps the cookie off every non-auth request while letting logout
actually revoke — and unlike putting `[Authorize]` on logout, it still works once the
15-minute access token has expired.

**Rotation with reuse detection.** Each refresh mints a new token and marks its predecessor
revoked via `ReplacedByTokenId`. Presenting an *already-revoked* token means it leaked, so
the entire family is revoked and the session ends. This is the security core of the slice.

**Only the hash is persisted**, as above.

### Request flow

```
browser (mp_access cookie)
  └─ JwtBearer handler, OnMessageReceived reads the token out of the cookie
      └─ HttpContext.User populated with NameIdentifier
          └─ CurrentUser.UserId          ← unchanged from slice 1
              └─ .Where(w => w.UserId == …)
```

`PriceHub` authenticates through the same path: cookies ride the SignalR negotiate and
websocket handshake, so no query-string access token is needed. The hub keeps broadcasting
to `Clients.All` — prices are public data — but unauthenticated connections are rejected.

### CSRF

Unsafe methods (POST/PUT/PATCH/DELETE) require an `X-CSRF-Token` header matching the
`mp_csrf` cookie. `SameSite` already blocks the common cross-site cases, so this is
defence in depth rather than the primary control — the ADR should say that plainly rather
than overclaiming it. Double-submit is chosen over the framework's `IAntiforgery`, which is
oriented around form posts and MVC views.

### Rate limiting and lockout

Two independent controls, because they defend different things:

- **Per-IP** fixed-window limiting on `/auth/login` and `/auth/register` — **10 requests
  per minute** — via .NET 10's built-in `RateLimiter` middleware. Defends the endpoint.
- **Per-account** lockout — **5 consecutive failures locks the account for 15 minutes**,
  tracked on `User`, with the counter reset on any successful login. Defends one account
  against an attempt distributed across many IPs.

Both surface as `429` with `Retry-After`. The concrete numbers live in configuration, so
the integration tests can drive the thresholds without a 15-minute wait.

---

## Error handling

| Failure | Response |
|---|---|
| Bad credentials | `401`, `/errors/invalid-credentials` — deliberately generic |
| Throttled or locked out | `429` + `Retry-After` |
| Missing or mismatched CSRF token | `403`, `/errors/csrf-failed` |
| Expired access token | `401` → client refreshes silently and retries once |
| **Reused refresh token** | `401`, `/errors/session-revoked`; family revoked |
| Email already registered | `409`, `/errors/email-taken` |
| Password fails policy | `400` with per-field errors |

All remain RFC 7807 ProblemDetails carrying the correlation ID, per slice 1.

This also closes a slice 1 ledger item: `UnauthorizedAccessException` from `CurrentUser`
previously fell through to a `500`. It now maps to `401`.

### Password policy

NIST SP 800-63B: 12 character minimum, no composition rules, no forced rotation, and a
blocklist check. Deliberately the opposite of the "one uppercase, one number, one symbol"
reflex.

The blocklist is the **SecLists 10,000-most-common-passwords corpus**, embedded in the
Application assembly. The alternative — querying the Have I Been Pwned range API — is the
stronger check, but it puts an outbound network call on the registration path and a
third-party availability dependency in the middle of a test suite. Recorded as a candidate
for the hardening slice.

**Measured limitation, stated rather than glossed** (found during implementation on
2026-08-03; the original text here specified "roughly the thousand most common passwords"):
only 10 of those 10,000 entries are 12 characters or longer, and none of the top 1,000 are.
Against a 12-character minimum the length rule alone rejects 9,990 of them before the
blocklist is ever consulted, so the blocklist is very nearly inert as configured. It is
kept because it is genuine breach data at no runtime cost, and it becomes load-bearing the
moment the minimum drops — but the honest summary is that **length is the control doing the
work here**, and only a full-corpus check like HIBP would meaningfully catch long breached
passwords.

### Accepted limitation: account enumeration

Registration inherently reveals whether an email is already registered. Generic messaging
on *login* plus rate limiting narrows the exposure; closing it properly requires an
email-confirmation flow, which is out of scope. Recorded here as a known gap rather than
quietly hoped over.

### Accepted limitation: the seeded dev account

The migration seeds `dev@marketpulse.local` with a watchlist of four tickers so a clean
clone opens on a live-looking screen. With real auth that account needs a password, which
means a password hash constant committed in a migration. This is accepted as a
showcase-only trade-off: the credentials are documented in the README, and removing the
seeded account is work for the hardening slice. Registration creates an empty watchlist,
so real users are unaffected.

---

## A constraint the deployment slice inherits

Cookie authentication requires the SPA and API to be **same-site**. In development they
are: `localhost:5173` and `localhost:5100` differ only by port, and ports are not part of a
"site", so slice 1's existing CORS configuration continues to work unchanged.

A production topology of `app.example.com` + `api.example.com` would silently stop sending
the cookies. The deployment slice must therefore either serve both from one site, or
introduce the BFF proxy hop. Written down now rather than discovered during a deploy.

---

## Testing

| Level | Tooling | Covers |
|---|---|---|
| Unit | xUnit | Password policy validator; refresh rotation and reuse detection; lockout state transitions; JWT claims and expiry |
| Integration | WebApplicationFactory + Testcontainers | Register → login → cookies set; protected route without cookie → `401`; refresh rotates the token; **reused refresh revokes the family**; **user A cannot read, add to, or delete from user B's watchlist**; missing CSRF → `403`; lockout after 5 consecutive failures; unauthenticated SignalR connection rejected |
| Frontend | Vitest + RTL + MSW | Login form validation and error rendering; protected-route redirect; single-flight refresh retry |
| E2E | Playwright | Register → login → add a ticker → watch it tick → logout → confirm `/` redirects |

**Cross-user isolation is the cheapest and most important addition.** Slice 1 scoped every
query by `UserId` but only ever had one user in the database, so a query missing its
`.Where` clause would pass every test currently in the suite.

**Playwright is the most expensive part of the slice.** CI needs SQL Server, the API and
the built dashboard running together, which makes the new job materially slower than the
current pipeline. It is still worth it: it is the only test that proves the cookie survives
a real browser round-trip, which no integration test can.

### Still deliberately not tested

- **No SignalR transport test** — unchanged from slice 1. That tests Microsoft's library.
- **No coverage threshold in CI** — unchanged from slice 1.
- **No load or brute-force simulation.** The rate limiter is tested for behaviour at its
  threshold, not under real concurrent load.

---

## Done criteria

Slice 2 is complete when, from a clean clone:

- [ ] Registering a new account in the browser yields a working, empty watchlist
- [ ] Logging in as the seeded dev user shows the four seeded tickers ticking
- [ ] The session survives a page refresh with no re-login
- [ ] Sitting past the 15-minute access-token expiry does not log the user out — the silent refresh works
- [ ] Logging out makes `/` redirect to `/login`
- [ ] A reused refresh token kills the session
- [ ] `DevAuthMiddleware.cs` no longer exists
- [ ] `dotnet test`, `pnpm test` and `pnpm e2e` all pass
- [ ] CI is green including the new E2E job
- [ ] ADR-003 is committed with a rejected-alternatives section
- [ ] `TESTING.md` no longer claims E2E is absent

---

## Interview category coverage

Read against the debt table in the slice 1 spec.

| # | Category | Slice 2 banks | Still owed |
|---|---|---|---|
| 2 | **JavaScript fundamentals** | Single-flight refresh via a module-scoped promise — a closure doing real production work | `AbortController`; `Promise.allSettled`; prototypes; the emitter package |
| 3 | **TypeScript** | Session state as a discriminated union; typed auth client surface | DTOs generated from OpenAPI; `Result<T, E>`; the full message union |
| 4 | **ASP.NET Core depth** | Options pattern with `ValidateOnStart()`; authentication middleware; rate-limiting middleware | Minimal APIs beside controllers; action filters; ADR-005 |
| 7 | **Web API design** ⭐ | JWT access + rotating refresh; cookie transport; CSRF; per-IP and per-account rate limiting | OIDC; policy-based authorization; idempotency keys; documented versioning strategy |
| 10 | **Architecture** ⭐ | The seam paying off: real auth with zero changes below the API layer | Modular monolith realised; micro-frontends; design system; ADR-004 |
| 13 | **Security** ⭐ | Password hashing; NIST-aligned policy; httpOnly/SameSite cookies; CSRF; reuse detection; brute-force defence; enumeration limitation documented | Secrets management; CSP; XSS/DOMPurify; least-privilege IAM; the threat model |
| 14 | **Testing** | **E2E/Playwright** — closing slice 1's recorded gap; cross-user isolation | TDD applied to the alert engine; coverage philosophy in practice |

Categories 7 and 13 move from barely-started to substantially banked, which is the point
of sequencing this slice second.

---

## What comes next

Slice 2 leaves three named follow-ons: **OIDC social login** (which the cookie transport
was chosen to accommodate), **policy-based authorization** (waiting on a real privileged
action), and the **BFF proxy hop** (waiting on a production topology). Each gets its own
spec.
