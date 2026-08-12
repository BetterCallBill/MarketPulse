# ADR-003: Cookie-based sessions with rotating refresh tokens

**Status:** Accepted · **Date:** 2026-08-03

## Context

Slice 1 shipped `DevAuthMiddleware`, a stub that stamped a fixed user id onto every
request so the watchlist could be scoped to *someone* while the real thing was deferred.
ADR-002 accepted that debt explicitly. Slice 2 pays it: a browser SPA needs real sessions —
registration, sign-in, a token lifecycle that survives a page reload, and a sign-out that
actually ends the session server-side.

The constraint that shapes everything below is that the client is a browser. Whatever
credential the SPA holds is reachable by any script running on the page.

## Decision

A short-lived JWT access token (15 minutes) plus a long-lived rotating refresh token
(14 days), both delivered as `httpOnly` cookies the JavaScript never sees.

- The access token is a signed JWT carrying `sub` (user id) and `email`. The JWT bearer
  handler is configured to read it from the `mp_access` cookie rather than the
  `Authorization` header.
- The refresh token is 256 bits of CSPRNG output. Only its SHA-256 hash is stored; the
  token itself lives in the `mp_refresh` cookie and nowhere else, so a database dump
  yields no usable sessions.
- Every refresh rotates: the presented token is revoked and records the id of its
  successor. Presenting an already-revoked token means it leaked, and revokes the user's
  entire token family.
- Because cookies are sent automatically, CSRF protection is required. It is double-submit:
  a non-`httpOnly` `mp_csrf` nonce that the client echoes in an `X-CSRF-Token` header,
  compared in constant time on every unsafe method.

## Rationale

Keeping the token out of JavaScript's reach is the point. The IETF *OAuth 2.0 for
Browser-Based Applications* BCP discourages holding tokens in the browser at all, precisely
because any XSS retrieves them. `httpOnly` means an injected script can still *make*
authenticated requests while the page is open, but cannot exfiltrate a credential to use
later from elsewhere — a materially smaller blast radius.

Short access-token lifetime plus refresh rotation bounds the damage further: a stolen
access token is useless in 15 minutes, and a stolen refresh token is detectable, because
its use by two parties makes one of them present a revoked token.

## Rejected alternatives

- **Bearer token in JavaScript memory.** The conventional SPA pattern, and simpler: no
  CSRF concern at all, since nothing is sent automatically. Rejected because it puts the
  credential exactly where XSS can read it, and because "in memory only" also means the
  session dies on every page reload unless it is mirrored into `localStorage`, which is
  strictly worse.
- **Full ASP.NET Core Identity.** It would supply registration, lockout, and token
  handling for free. Rejected because `IdentityUser<Guid>` is an Infrastructure type:
  adopting it means either a second parallel user model kept in sync with `Domain.User`,
  or a `Domain` → Identity reference that breaks the dependency-rule test. We took the
  audited `PasswordHasher<T>` — which is constrained to `class`, not to `IdentityUser` —
  and nothing else.
- **An external IdP (Entra ID, Auth0, Keycloak).** What most enterprises actually do, and
  the right call for a real product: someone else runs the hard parts. Rejected here
  because delegating means writing almost no authentication code, and the token lifecycle
  is the thing this project exists to demonstrate.
- **Opaque server-side sessions.** Simpler, revocable by definition, and arguably the
  better engineering choice for a single API with no other consumers. Rejected because it
  banks none of the JWT signing, claims, and validation material, and because the refresh
  rotation and reuse-detection design is the more interesting problem.

## Consequences

**Cookie auth requires same-site deployment.** Splitting the app across `app.example.com`
and `api.example.com` would silently stop sending the cookies under `SameSite=Strict`.
The deployment slice must therefore serve both from one origin, or add a
backend-for-frontend proxy hop. This is a deliberate step toward BFF, not an accident of
the design — but it is a real constraint on the deployment topology and must not be
discovered late.

**SignalR authenticates off the same cookie.** The hub connection carries `mp_access`
automatically, so no separate token negotiation exists. The same-origin constraint applies
to the hub too.

**The refresh cookie's path is `/api/v1/auth`, not `/api/v1/auth/refresh`.** Scoping it to
the single endpoint would mean logout never receives the cookie and so could never revoke
the session.

**`Secure` is set only outside Development.** .NET's `CookieContainer` refuses to send
`Secure` cookies over plain HTTP, which would break every integration test and the
Playwright suite. The attribute is covered as a pure function by `AuthCookiesTests`
instead of end to end — see `docs/TESTING.md`.

## Accepted limitations

- **Account enumeration on registration.** Login is deliberately indistinguishable between
  an unknown email and a wrong password, and equalises timing by verifying against a dummy
  hash. Registration cannot hide the same fact without an email round trip, so a "that
  email is taken" response remains an enumeration oracle. Accepted for a showcase; the
  fix is confirmation-email registration.
- **A seeded development account** (`dev@marketpulse.local`) whose PBKDF2 hash is a
  committed constant, because `HasData` requires determinism and `PasswordHasher<T>` salts
  randomly. Documented in the README and removed by the hardening slice.
- **The password blocklist is very nearly inert.** Only 10 of the 10,000 most common
  passwords are 12 characters or longer, so the length minimum rejects 9,990 of them before
  the blocklist is consulted. It is retained because it is real breach data at no cost and
  is already correct if the minimum ever drops.
