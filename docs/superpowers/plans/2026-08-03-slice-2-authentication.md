# Slice 2 — Authentication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the development identity stub with real email/password authentication — JWT access tokens and rotating refresh tokens carried in httpOnly cookies — without changing any repository, handler, or watchlist query below the API layer.

**Architecture:** Slice 1 built `ICurrentUser` to read `ClaimTypes.NameIdentifier` off `HttpContext.User` and nothing below the API layer knows where that claim came from. This slice deletes `DevAuthMiddleware`, adds a JWT bearer handler that reads its token from an httpOnly cookie, and populates the same claim. Refresh tokens rotate on every use, are stored only as SHA-256 hashes, and trigger revocation of the whole family when a revoked token is replayed. CSRF is double-submit; brute force is defended per-IP (rate limiter) and per-account (lockout).

**Tech Stack:** .NET 10, ASP.NET Core, EF Core 10 + SQL Server, MediatR, FluentValidation, `Microsoft.AspNetCore.Authentication.JwtBearer`, `PasswordHasher<T>` from `Microsoft.Extensions.Identity.Core`, React 18 + Vite + TanStack Query + react-router-dom, zod, xUnit + Testcontainers, Vitest + RTL + MSW, Playwright.

**Spec:** [docs/superpowers/specs/2026-08-02-authentication-design.md](../specs/2026-08-02-authentication-design.md)

## Global Constraints

- **Branch:** work on `feature/slice-2-authentication`, cut from `test`. Merge target is `test`, never `main`.
- **Commit rule:** NO `Co-Authored-By` trailer on any commit. Conventional-commit prefixes (`feat:`, `fix:`, `test:`, `docs:`, `chore:`, `ci:`).
- **`TreatWarningsAsErrors` is true** for every .NET project (`Directory.Build.props`). A warning fails the build. Notably: `xUnit1031` (blocking `.Result`/`.Wait()`) and `CS8618` (non-nullable uninitialised) are build errors.
- **Central Package Management is on.** Never put a `Version=` on a `PackageReference`. `dotnet add package` writes the version into `Directory.Packages.props`. Never write a wildcard (`12.*`) — CPM rejects it. After every `dotnet add package`, confirm `Directory.Packages.props` still ends with a trailing newline; `dotnet add package` has stripped it twice before.
- **Dependency rule, enforced by `tests/MarketPulse.UnitTests/Architecture/DependencyRuleTests.cs`:** `Domain` references nothing; `Application` references `Domain`; `Infrastructure` references `Application`; `Api` references `Infrastructure`. **No Identity, EF Core, or ASP.NET type may appear in `Domain` or `Application`.**
- **The watchlist feature does not change.** These files are not to be edited: `IWatchlistRepository`, `WatchlistRepository`, `GetWatchlistQuery`, `AddWatchlistItemCommand`, `RemoveWatchlistItemCommand`, `Watchlist.cs`, `WatchlistItem.cs`, and the `Watchlist`/`WatchlistItem` mapping blocks in `MarketPulseDbContext`. The only permitted edit to `WatchlistController` is adding `[Authorize]`. (Adding *new* files below the API layer — the auth entities, handlers and repositories — is the substance of Tasks 1–5 and is expected; the constraint is about not disturbing slice 1's watchlist path.)
- **Token lifetimes:** access 15 minutes, refresh 14 days. **Lockout:** 5 consecutive failures → 15 minutes. **Rate limit:** 10 requests/minute per IP on login and register. All are configuration-bound so tests can override them.
- **Cookie names:** `mp_access`, `mp_refresh`, `mp_csrf`. Refresh cookie path is `/api/v1/auth/refresh`. `Secure` is set only outside Development — .NET's `CookieContainer` refuses to send `Secure` cookies over plain HTTP, which would break every integration test.
- **Dev seed credentials:** `dev@marketpulse.local` / `DevPassw0rd!2026`. The password hash is a literal constant in `SeedData` because `PasswordHasher<T>` salts randomly and `HasData` requires determinism.

---

## File structure

**Domain** (`src/MarketPulse.Domain/`)
- `Entities/User.cs` — *modified*: password hash, lockout counters, and the behaviour that maintains them
- `Entities/RefreshToken.cs` — *new*: one refresh token with its rotation/revocation invariants
- `Exceptions/DomainException.cs` — *modified*: gains a `StatusCode` so auth failures are not all 409
- `Exceptions/AuthExceptions.cs` — *new*: invalid credentials, account locked, email taken, session revoked, CSRF failed

**Application** (`src/MarketPulse.Application/`)
- `Abstractions/IPasswordHasher.cs`, `ITokenService.cs`, `IUserRepository.cs`, `IRefreshTokenRepository.cs` — *new*
- `Configuration/JwtOptions.cs`, `AuthOptions.cs` — *new*
- `Authentication/PasswordPolicy.cs` + `CommonPasswords.txt` — *new*: NIST-aligned policy and the blocklist
- `Authentication/RegisterUserCommand.cs`, `LoginCommand.cs`, `RefreshSessionCommand.cs`, `LogoutCommand.cs` — *new*: one file per use case, each holding its command, validator and handler (the shape `Watchlists/` already uses)
- `Authentication/AuthResult.cs` — *new*: what the handlers hand back to the API layer

**Infrastructure** (`src/MarketPulse.Infrastructure/`)
- `Authentication/PasswordHasherAdapter.cs`, `JwtTokenService.cs` — *new*
- `Persistence/UserRepository.cs`, `RefreshTokenRepository.cs` — *new*
- `Persistence/MarketPulseDbContext.cs`, `SeedData.cs` — *modified*
- `Persistence/Migrations/*_AddAuthentication.cs` — *new*, generated

**Api** (`src/MarketPulse.Api/`)
- `Authentication/AuthCookies.cs` — *new*: cookie names and a pure options builder (unit-testable)
- `Controllers/AuthController.cs` — *new*
- `Middleware/CsrfMiddleware.cs` — *new*
- `Middleware/DevAuthMiddleware.cs` — **deleted**
- `Middleware/ExceptionHandlingMiddleware.cs`, `Program.cs`, `Controllers/WatchlistController.cs`, `Hubs/PriceHub.cs`, `appsettings*.json` — *modified*

**Frontend**
- `packages/api-client/src/client.ts` — *modified*: cookies, CSRF header, single-flight 401→refresh→retry
- `packages/api-client/src/schemas.ts` — *modified*: session schema
- `apps/dashboard/src/features/auth/` — *new*: `useSession.ts`, `LoginScreen.tsx`, `RegisterScreen.tsx`, `ProtectedRoute.tsx`
- `apps/dashboard/src/App.tsx` — *modified*: router

**Tests**
- `tests/MarketPulse.UnitTests/Domain/UserTests.cs`, `RefreshTokenTests.cs`, `Application/PasswordPolicyTests.cs`, `Api/AuthCookiesTests.cs`, `Infrastructure/JwtTokenServiceTests.cs` — *new*
- `tests/MarketPulse.IntegrationTests/AuthApiTests.cs`, `CsrfAndRateLimitTests.cs`, `CrossUserIsolationTests.cs`, `AuthenticatedClient.cs` — *new*
- `tests/e2e/` — *new* pnpm workspace package with Playwright

---

## Task ordering note

Tasks 1–5 add code without changing behaviour: the API keeps using `DevAuthMiddleware` and every existing test stays green. **Task 6 is the swap** — it deletes the stub, turns on `[Authorize]`, and updates the slice 1 integration tests in the same commit, because any split of that work leaves the build red. Tasks 7–12 layer protections, tests, the frontend, and docs on top.

### Task 1: Domain — credentials, lockout, and refresh tokens

**Files:**
- Modify: `src/MarketPulse.Domain/Entities/User.cs`
- Create: `src/MarketPulse.Domain/Entities/RefreshToken.cs`
- Modify: `src/MarketPulse.Domain/Exceptions/DomainException.cs`
- Create: `src/MarketPulse.Domain/Exceptions/AuthExceptions.cs`
- Test: `tests/MarketPulse.UnitTests/Domain/UserTests.cs`
- Test: `tests/MarketPulse.UnitTests/Domain/RefreshTokenTests.cs`

**Interfaces:**
- Consumes: nothing (first task).
- Produces:
  - `User.Register(string email, string passwordHash) : User` — normalises email to lowercase, sets `CreatedUtc`
  - `User.Email`, `User.PasswordHash`, `User.FailedLoginCount`, `User.LockoutEndUtc` (all `{ get; private set; }`)
  - `User.IsLockedOut(DateTimeOffset now) : bool`
  - `User.RecordFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration) : void`
  - `User.RecordSuccessfulLogin() : void`
  - `RefreshToken.Issue(Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime) : RefreshToken`
  - `RefreshToken.Id`, `.UserId`, `.TokenHash`, `.ExpiresUtc`, `.CreatedUtc`, `.RevokedUtc`, `.ReplacedByTokenId`
  - `RefreshToken.IsActive(DateTimeOffset now) : bool`
  - `RefreshToken.Revoke(DateTimeOffset now) : void`
  - `RefreshToken.ReplaceWith(Guid replacementId, DateTimeOffset now) : void`
  - `DomainException.StatusCode : int` (virtual, default `409`)
  - `InvalidCredentialsException`, `AccountLockedException(TimeSpan retryAfter)` with `.RetryAfter`, `EmailTakenException`, `SessionRevokedException`, `CsrfValidationException`

- [ ] **Step 1: Cut the feature branch**

```bash
git checkout test
git pull --ff-only
git checkout -b feature/slice-2-authentication
```

- [ ] **Step 2: Write the failing `User` tests**

Create `tests/MarketPulse.UnitTests/Domain/UserTests.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.UnitTests.Domain;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Register_lowercases_the_email()
    {
        var user = User.Register("Dev@MarketPulse.Local", "hash");

        Assert.Equal("dev@marketpulse.local", user.Email);
    }

    [Fact]
    public void A_new_user_is_not_locked_out()
    {
        var user = User.Register("a@b.com", "hash");

        Assert.False(user.IsLockedOut(Now));
        Assert.Equal(0, user.FailedLoginCount);
    }

    [Fact]
    public void Failures_below_the_threshold_do_not_lock_the_account()
    {
        var user = User.Register("a@b.com", "hash");

        for (var i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        Assert.Equal(4, user.FailedLoginCount);
        Assert.False(user.IsLockedOut(Now));
    }

    [Fact]
    public void The_fifth_consecutive_failure_locks_the_account()
    {
        var user = User.Register("a@b.com", "hash");

        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        Assert.True(user.IsLockedOut(Now));
        Assert.Equal(Now.AddMinutes(15), user.LockoutEndUtc);
    }

    [Fact]
    public void The_lockout_expires()
    {
        var user = User.Register("a@b.com", "hash");
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        Assert.False(user.IsLockedOut(Now.AddMinutes(16)));
    }

    [Fact]
    public void A_successful_login_resets_the_counter_and_the_lockout()
    {
        var user = User.Register("a@b.com", "hash");
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(Now, maxAttempts: 5, lockoutDuration: TimeSpan.FromMinutes(15));
        }

        user.RecordSuccessfulLogin();

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockoutEndUtc);
        Assert.False(user.IsLockedOut(Now));
    }
}
```

- [ ] **Step 3: Run the tests and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~UserTests`
Expected: build failure — `User` has no `Register`, `IsLockedOut`, `RecordFailedLogin`, or `RecordSuccessfulLogin`.

- [ ] **Step 4: Rewrite `User`**

Replace the whole of `src/MarketPulse.Domain/Entities/User.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

public sealed class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockoutEndUtc { get; private set; }

    private User() { }

    /// <summary>
    /// Used by the seed data, which needs a fixed id, and by tests. Application code
    /// registers users through <see cref="Register"/>.
    /// </summary>
    public User(Guid id, string email, string passwordHash, DateTimeOffset createdUtc)
    {
        Id = id;
        Email = Normalise(email);
        PasswordHash = passwordHash;
        CreatedUtc = createdUtc;
    }

    public static User Register(string email, string passwordHash) => new(
        Guid.NewGuid(), email, passwordHash, DateTimeOffset.UtcNow);

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndUtc is { } end && end > now;

    public void RecordFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;

        if (FailedLoginCount >= maxAttempts)
        {
            LockoutEndUtc = now + lockoutDuration;
        }
    }

    public void RecordSuccessfulLogin()
    {
        FailedLoginCount = 0;
        LockoutEndUtc = null;
    }

    /// <summary>Emails are compared and stored case-insensitively.</summary>
    public static string Normalise(string email) => email.Trim().ToLowerInvariant();
}
```

- [ ] **Step 5: Run the `User` tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~UserTests`
Expected: 6 passed.

Note: the whole solution will **not** build yet — `MarketPulseDbContext` and `WatchlistApiTests` still call the old two-argument `User` constructor. Task 4 fixes the DbContext and Task 6 fixes the test. That is expected; do not chase it here.

- [ ] **Step 6: Write the failing `RefreshToken` tests**

Create `tests/MarketPulse.UnitTests/Domain/RefreshTokenTests.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.UnitTests.Domain;

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Fortnight = TimeSpan.FromDays(14);

    private static RefreshToken Issue() =>
        RefreshToken.Issue(Guid.NewGuid(), "hash", Now, Fortnight);

    [Fact]
    public void A_freshly_issued_token_is_active()
    {
        var token = Issue();

        Assert.True(token.IsActive(Now));
        Assert.Equal(Now.Add(Fortnight), token.ExpiresUtc);
        Assert.Null(token.RevokedUtc);
        Assert.Null(token.ReplacedByTokenId);
    }

    [Fact]
    public void An_expired_token_is_not_active()
    {
        var token = Issue();

        Assert.False(token.IsActive(Now.Add(Fortnight).AddSeconds(1)));
    }

    [Fact]
    public void A_revoked_token_is_not_active()
    {
        var token = Issue();

        token.Revoke(Now);

        Assert.False(token.IsActive(Now));
        Assert.Equal(Now, token.RevokedUtc);
    }

    [Fact]
    public void Replacing_a_token_revokes_it_and_records_the_successor()
    {
        var token = Issue();
        var replacementId = Guid.NewGuid();

        token.ReplaceWith(replacementId, Now);

        Assert.False(token.IsActive(Now));
        Assert.Equal(replacementId, token.ReplacedByTokenId);
        Assert.Equal(Now, token.RevokedUtc);
    }

    [Fact]
    public void Revoking_twice_keeps_the_first_revocation_time()
    {
        var token = Issue();
        token.Revoke(Now);

        token.Revoke(Now.AddHours(1));

        Assert.Equal(Now, token.RevokedUtc);
    }
}
```

- [ ] **Step 7: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~RefreshTokenTests`
Expected: build failure — `RefreshToken` does not exist.

- [ ] **Step 8: Create `RefreshToken`**

Create `src/MarketPulse.Domain/Entities/RefreshToken.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

/// <summary>
/// One issued refresh token. Only the SHA-256 hash of the token is ever stored — the
/// token itself exists in the response cookie and nowhere else, so a database dump
/// yields no usable sessions.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset ExpiresUtc { get; private set; }
    public DateTimeOffset? RevokedUtc { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(
        Guid userId, string tokenHash, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = tokenHash,
        CreatedUtc = now,
        ExpiresUtc = now + lifetime
    };

    public bool IsActive(DateTimeOffset now) => RevokedUtc is null && ExpiresUtc > now;

    public void Revoke(DateTimeOffset now) => RevokedUtc ??= now;

    /// <summary>
    /// Rotation: revoke this token and record which token superseded it. The successor
    /// id is what makes a replayed token traceable to a live family.
    /// </summary>
    public void ReplaceWith(Guid replacementId, DateTimeOffset now)
    {
        Revoke(now);
        ReplacedByTokenId = replacementId;
    }
}
```

- [ ] **Step 9: Run the `RefreshToken` tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~RefreshTokenTests`
Expected: 5 passed.

- [ ] **Step 10: Give `DomainException` a status code**

The existing middleware maps every `DomainException` to 409. Auth failures need 401, 403 and 429, so the status moves onto the exception. The three existing exceptions inherit the 409 default and keep behaving exactly as before.

In `src/MarketPulse.Domain/Exceptions/DomainException.cs`, replace the base class only (leave `DuplicateTickerException`, `WatchlistFullException` and `TickerNotOnWatchlistException` untouched):

```csharp
public abstract class DomainException(string message) : Exception(message)
{
    /// <summary>Stable slug used to build the ProblemDetails `type` URI.</summary>
    public abstract string ErrorCode { get; }

    /// <summary>
    /// HTTP status this failure maps to. Watchlist rule violations are conflicts, so 409
    /// is the default; the authentication exceptions override it.
    /// </summary>
    public virtual int StatusCode => 409;
}
```

- [ ] **Step 11: Add the authentication exceptions**

Create `src/MarketPulse.Domain/Exceptions/AuthExceptions.cs`:

```csharp
namespace MarketPulse.Domain.Exceptions;

/// <summary>
/// Deliberately identical whether the email is unknown or the password is wrong —
/// distinguishing them hands an attacker an account-enumeration oracle.
/// </summary>
public sealed class InvalidCredentialsException()
    : DomainException("Email or password is incorrect.")
{
    public override string ErrorCode => "invalid-credentials";
    public override int StatusCode => 401;
}

public sealed class AccountLockedException(TimeSpan retryAfter)
    : DomainException("Too many failed attempts. Try again later.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
    public override string ErrorCode => "account-locked";
    public override int StatusCode => 429;
}

public sealed class EmailTakenException()
    : DomainException("That email address is already registered.")
{
    public override string ErrorCode => "email-taken";
}

/// <summary>
/// Raised when an already-revoked refresh token is presented, which means it leaked.
/// The handler revokes the user's whole token family in response.
/// </summary>
public sealed class SessionRevokedException()
    : DomainException("This session is no longer valid. Sign in again.")
{
    public override string ErrorCode => "session-revoked";
    public override int StatusCode => 401;
}

public sealed class CsrfValidationException()
    : DomainException("Missing or invalid CSRF token.")
{
    public override string ErrorCode => "csrf-failed";
    public override int StatusCode => 403;
}
```

- [ ] **Step 12: Confirm the Domain and unit test projects build**

Run: `dotnet build src/MarketPulse.Domain && dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~UserTests|FullyQualifiedName~RefreshTokenTests"`
Expected: build succeeds, 11 passed.

- [ ] **Step 13: Commit**

```bash
git add src/MarketPulse.Domain tests/MarketPulse.UnitTests/Domain
git commit -m "feat: add credentials, lockout, and refresh tokens to the domain"
```

---

### Task 2: Application — abstractions, options, and the password policy

**Files:**
- Create: `src/MarketPulse.Application/Abstractions/IPasswordHasher.cs`
- Create: `src/MarketPulse.Application/Abstractions/ITokenService.cs`
- Create: `src/MarketPulse.Application/Abstractions/IUserRepository.cs`
- Create: `src/MarketPulse.Application/Abstractions/IRefreshTokenRepository.cs`
- Create: `src/MarketPulse.Application/Configuration/JwtOptions.cs`
- Create: `src/MarketPulse.Application/Configuration/AuthOptions.cs`
- Create: `src/MarketPulse.Application/Authentication/PasswordPolicy.cs`
- Create: `src/MarketPulse.Application/Authentication/CommonPasswords.txt`
- Create: `src/MarketPulse.Application/Authentication/AuthResult.cs`
- Modify: `src/MarketPulse.Application/MarketPulse.Application.csproj`
- Test: `tests/MarketPulse.UnitTests/Application/PasswordPolicyTests.cs`

**Interfaces:**
- Consumes: `User`, `RefreshToken` from Task 1.
- Produces:
  - `IPasswordHasher.Hash(string password) : string`
  - `IPasswordHasher.Verify(string hash, string password) : bool`
  - `ITokenService.CreateAccessToken(User user) : string`
  - `ITokenService.CreateRefreshToken() : (string Token, string Hash)`
  - `ITokenService.HashRefreshToken(string token) : string`
  - `IUserRepository`: `GetByEmailAsync`, `GetByIdAsync`, `EmailExistsAsync`, `AddAsync`, `SaveChangesAsync`
  - `IRefreshTokenRepository`: `GetByHashAsync`, `AddAsync`, `RevokeAllForUserAsync`, `SaveChangesAsync`
  - `JwtOptions` with `SectionName`, `SigningKey`, `Issuer`, `Audience`, `AccessTokenLifetime`, `RefreshTokenLifetime`
  - `AuthOptions` with `SectionName`, `MaxFailedAttempts`, `LockoutDuration`, `LoginRequestsPerMinute`
  - `PasswordPolicy.MinimumLength : int`, `PasswordPolicy.IsAcceptable(string password) : bool`
  - `AuthResult` record

- [ ] **Step 0: Unblock the build**

Task 1 changed the `User` constructor, so `MarketPulseDbContext` no longer compiles — and because `MarketPulse.UnitTests` references `MarketPulse.Infrastructure`, **no unit test in this repo can run until this is fixed.** Do this before anything else, or every TDD step in this task and the next is blind.

First generate the seeded dev user's password hash. `PasswordHasher<T>` salts randomly, so it cannot be computed inside `HasData` and must be a committed constant. Add a throwaway test that fails on purpose to print it:

```csharp
// tests/MarketPulse.UnitTests/Infrastructure/HashGen.cs  (DELETE after this step)
using Microsoft.AspNetCore.Identity;
using MarketPulse.Domain.Entities;

namespace MarketPulse.UnitTests.Infrastructure;

public class HashGen
{
    [Fact]
    public void Print()
    {
        var user = new User(Guid.Empty, "x@y.z", string.Empty, DateTimeOffset.UnixEpoch);
        Assert.Fail(new PasswordHasher<User>().HashPassword(user, "DevPassw0rd!2026"));
    }
}
```

This needs the hasher package on the Infrastructure project (Task 3 Step 1 adds it too; adding it here is fine and idempotent):

```bash
dotnet add src/MarketPulse.Infrastructure package Microsoft.Extensions.Identity.Core
```

Verify `Directory.Packages.props` got a concrete version, that the `.csproj` `PackageReference` carries no `Version` attribute, and that the props file still ends with a newline (`tail -c 1 Directory.Packages.props | xxd | tail -1` → `0a`).

Run `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~HashGen`, copy the `AQAAAA…` value from the failure message, then **delete `HashGen.cs`**.

Now add to `src/MarketPulse.Infrastructure/Persistence/SeedData.cs`, below `DevUserEmail`:

```csharp
    /// <summary>
    /// Development-only credentials, documented in the README so a clean clone can sign in.
    /// Accepted showcase trade-off, recorded in the slice 2 spec: the hash must be a literal
    /// because `PasswordHasher<T>` salts randomly and `HasData` requires determinism.
    /// The hardening slice removes this account.
    /// </summary>
    public const string DevUserPassword = "DevPassw0rd!2026";

    /// <summary>PBKDF2 hash of <see cref="DevUserPassword"/>. Regenerate only if that changes.</summary>
    public const string DevUserPasswordHash = "PASTE_THE_GENERATED_HASH_HERE";

    /// <summary>Fixed creation timestamp — `HasData` must be deterministic across regenerations.</summary>
    public static readonly DateTimeOffset DevUserCreatedUtc =
        new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
```

Then in `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`, change **only** the seed line inside the existing `b.Entity<User>` block:

```csharp
            e.HasData(new User(
                SeedData.DevUserId,
                SeedData.DevUserEmail,
                SeedData.DevUserPasswordHash,
                SeedData.DevUserCreatedUtc));
```

Leave the rest of that block alone — Task 4 adds the new column mappings and the `RefreshTokens` table.

Verify the build is unblocked:

```bash
dotnet test tests/MarketPulse.UnitTests
```

Expected: builds, and all slice 1 unit tests plus Task 1's 11 new tests pass. `MarketPulse.IntegrationTests` will still not build — Task 4 repairs it. Do not touch it here.

Commit this separately so the unblock is legible in history:

```bash
git add src/MarketPulse.Infrastructure Directory.Packages.props
git commit -m "fix: seed the dev user with a password hash so the build compiles"
```

- [ ] **Step 1: Write the failing password policy tests**

Create `tests/MarketPulse.UnitTests/Application/PasswordPolicyTests.cs`:

```csharp
using MarketPulse.Application.Authentication;

namespace MarketPulse.UnitTests.Application;

public class PasswordPolicyTests
{
    [Fact]
    public void The_minimum_length_is_twelve()
    {
        Assert.Equal(12, PasswordPolicy.MinimumLength);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Passwords_below_the_minimum_are_rejected(string password)
    {
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void A_long_passphrase_with_no_symbols_is_accepted()
    {
        // NIST SP 800-63B: length is the control that matters; composition rules are
        // explicitly discouraged. This must pass, or we have re-implemented the thing
        // the spec rejected.
        Assert.True(PasswordPolicy.IsAcceptable("correct horse battery staple"));
    }

    [Theory]
    [InlineData("unbelievable")]
    [InlineData("UNBELIEVABLE")]
    public void Blocklisted_passwords_are_rejected_case_insensitively(string password)
    {
        // One of only ten entries in the corpus long enough to reach the blocklist at all.
        Assert.False(PasswordPolicy.IsAcceptable(password));
    }

    [Fact]
    public void The_blocklist_actually_loaded()
    {
        Assert.True(PasswordPolicy.BlocklistSize > 5000,
            $"Blocklist only has {PasswordPolicy.BlocklistSize} entries — the embedded " +
            "resource probably did not load.");
    }

    [Fact]
    public void A_short_blocklisted_password_is_rejected_on_length_before_the_blocklist_matters()
    {
        // Documents a real limitation: of the 10,000 most common passwords, only 10 are
        // long enough to reach the blocklist at all. The 12-character minimum is doing
        // nearly all the work here — the blocklist earns its place only if that floor
        // ever drops. See the slice 2 spec's password-policy section.
        Assert.False(PasswordPolicy.IsAcceptable("password"));
        Assert.True("password".Length < PasswordPolicy.MinimumLength);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~PasswordPolicyTests`
Expected: build failure — `PasswordPolicy` does not exist.

- [ ] **Step 3: Fetch the blocklist**

```bash
curl -fsSL -o src/MarketPulse.Application/Authentication/CommonPasswords.txt \
  https://raw.githubusercontent.com/danielmiessler/SecLists/master/Passwords/Common-Credentials/10k-most-common.txt
wc -l < src/MarketPulse.Application/Authentication/CommonPasswords.txt
```

Expected: `10000`.

**Do not hand-write a substitute if this fails.** Invented entries are not breach data and give a false impression of the control's strength. If the URL is dead, try the sibling files in the same SecLists directory (`500-worst-passwords.txt`, `Pwdb_top-100000.txt`) and record which one you used.

**Known limitation, deliberately accepted — do not try to "fix" it:** only 10 of these 10,000 entries are ≥12 characters, and none of the top 1,000 are. At `MinimumLength = 12` the length check alone rejects 9,990 of them before the blocklist is consulted. The blocklist is therefore very nearly inert today. It is retained anyway because it is real breach data at no cost, and because it is already correct if the minimum ever drops. Task 12 documents this honestly rather than implying stronger protection than exists.

- [ ] **Step 4: Embed the blocklist in the assembly**

In `src/MarketPulse.Application/MarketPulse.Application.csproj`, add inside a new `ItemGroup`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Authentication\CommonPasswords.txt" />
  </ItemGroup>
```

- [ ] **Step 5: Write `PasswordPolicy`**

Create `src/MarketPulse.Application/Authentication/PasswordPolicy.cs`:

```csharp
using System.Reflection;

namespace MarketPulse.Application.Authentication;

/// <summary>
/// NIST SP 800-63B: length plus a blocklist, no composition rules, no forced rotation.
/// Composition rules ("one uppercase, one symbol") are what the guidance advises
/// against — they push users toward predictable mutations like `Password1!`.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    private static readonly HashSet<string> Blocklist = LoadBlocklist();

    public static int BlocklistSize => Blocklist.Count;

    public static bool IsAcceptable(string password) =>
        !string.IsNullOrWhiteSpace(password)
        && password.Length >= MinimumLength
        && !Blocklist.Contains(password);

    private static HashSet<string> LoadBlocklist()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("CommonPasswords.txt", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                set.Add(line.Trim());
            }
        }

        return set;
    }
}
```

- [ ] **Step 6: Run the policy tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~PasswordPolicyTests`
Expected: 7 passed.

- [ ] **Step 7: Add the abstractions**

Create `src/MarketPulse.Application/Abstractions/IPasswordHasher.cs`:

```csharp
namespace MarketPulse.Application.Abstractions;

public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>Constant-time verification. Returns false rather than throwing on a malformed hash.</summary>
    bool Verify(string hash, string password);
}
```

Create `src/MarketPulse.Application/Abstractions/ITokenService.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface ITokenService
{
    /// <summary>Signed JWT carrying the user id as `sub`. Lifetime comes from JwtOptions.</summary>
    string CreateAccessToken(User user);

    /// <summary>
    /// A new 256-bit random token and its SHA-256 hash. The caller stores the hash and
    /// returns the token to the browser.
    /// </summary>
    (string Token, string Hash) CreateRefreshToken();

    /// <summary>Hashes a token presented by a client, so it can be looked up.</summary>
    string HashRefreshToken(string token);
}
```

Create `src/MarketPulse.Application/Abstractions/IUserRepository.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> GetByEmailAsync(string email, CancellationToken ct);
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
```

Create `src/MarketPulse.Application/Abstractions/IRefreshTokenRepository.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct);
    Task AddAsync(RefreshToken token, CancellationToken ct);

    /// <summary>
    /// Revokes every still-active token for the user. This is the "family" in
    /// reuse detection: one replayed token kills every live session that user has.
    /// </summary>
    Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
```

- [ ] **Step 8: Add the options types**

Create `src/MarketPulse.Application/Configuration/JwtOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC-SHA256 signing key. 32 characters is the minimum for a 256-bit key.</summary>
    [Required, MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(14);
}
```

Create `src/MarketPulse.Application/Configuration/AuthOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(1, 100)]
    public int MaxFailedAttempts { get; init; } = 5;

    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-IP fixed window on login and register.</summary>
    [Range(1, 10_000)]
    public int LoginRequestsPerMinute { get; init; } = 10;
}
```

- [ ] **Step 9: Add the handler result type**

Create `src/MarketPulse.Application/Authentication/AuthResult.cs`:

```csharp
namespace MarketPulse.Application.Authentication;

/// <summary>
/// What the authentication handlers hand back. Deliberately transport-agnostic — the
/// API layer decides these become cookies, and mints the CSRF nonce separately.
/// </summary>
public sealed record AuthResult(
    Guid UserId,
    string Email,
    string AccessToken,
    DateTimeOffset AccessExpiresUtc,
    string RefreshToken,
    DateTimeOffset RefreshExpiresUtc);
```

- [ ] **Step 10: Build and confirm the architecture test still passes**

Run: `dotnet build src/MarketPulse.Application && dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~PasswordPolicyTests|FullyQualifiedName~DependencyRuleTests"`
Expected: build succeeds, all passed. The dependency-rule test proves no ASP.NET or EF type leaked into Application.

- [ ] **Step 11: Commit**

```bash
git add src/MarketPulse.Application tests/MarketPulse.UnitTests/Application
git commit -m "feat: add auth abstractions, options, and a NIST-aligned password policy"
```

---

### Task 3: Infrastructure — password hashing and token minting

**Files:**
- Create: `src/MarketPulse.Infrastructure/Authentication/PasswordHasherAdapter.cs`
- Create: `src/MarketPulse.Infrastructure/Authentication/JwtTokenService.cs`
- Modify: `src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj`
- Modify: `Directory.Packages.props` (via `dotnet add package`)
- Test: `tests/MarketPulse.UnitTests/Infrastructure/JwtTokenServiceTests.cs`
- Test: `tests/MarketPulse.UnitTests/Infrastructure/PasswordHasherAdapterTests.cs`

**Interfaces:**
- Consumes: `IPasswordHasher`, `ITokenService`, `JwtOptions` (Task 2); `User` (Task 1).
- Produces: `PasswordHasherAdapter : IPasswordHasher`, `JwtTokenService : ITokenService`. Both are registered in Task 4's DI step. The access token carries `sub` = user id and `email`.

- [ ] **Step 1: Add the two packages**

```bash
dotnet add src/MarketPulse.Infrastructure package Microsoft.Extensions.Identity.Core
dotnet add src/MarketPulse.Infrastructure package Microsoft.IdentityModel.JsonWebTokens
```

Do not pass `--version`; let NuGet resolve. Then confirm both landed in `Directory.Packages.props` with concrete versions (no wildcards), that the `.csproj` `PackageReference` entries carry **no** `Version` attribute, and that `Directory.Packages.props` still ends with a newline:

```bash
grep -E "Identity.Core|JsonWebTokens" Directory.Packages.props src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj
tail -c 1 Directory.Packages.props | xxd | tail -1
```

Expected: versions present in the props file only; the last byte is `0a`. If the newline is gone, restore it.

- [ ] **Step 2: Write the failing hasher tests**

Create `tests/MarketPulse.UnitTests/Infrastructure/PasswordHasherAdapterTests.cs`:

```csharp
using MarketPulse.Infrastructure.Authentication;

namespace MarketPulse.UnitTests.Infrastructure;

public class PasswordHasherAdapterTests
{
    private readonly PasswordHasherAdapter _hasher = new();

    [Fact]
    public void A_hash_verifies_against_its_own_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.True(_hasher.Verify(hash, "correct horse battery staple"));
    }

    [Fact]
    public void A_hash_does_not_verify_against_a_different_password()
    {
        var hash = _hasher.Hash("correct horse battery staple");

        Assert.False(_hasher.Verify(hash, "incorrect horse battery staple"));
    }

    [Fact]
    public void The_same_password_hashes_differently_each_time()
    {
        // Per-hash random salt. Two identical passwords must not produce identical hashes,
        // or the database leaks which accounts share a password.
        Assert.NotEqual(_hasher.Hash("correct horse battery staple"),
                        _hasher.Hash("correct horse battery staple"));
    }

    [Fact]
    public void A_malformed_hash_returns_false_rather_than_throwing()
    {
        Assert.False(_hasher.Verify("not-a-real-hash", "anything at all"));
    }
}
```

- [ ] **Step 3: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~PasswordHasherAdapterTests`
Expected: build failure — `PasswordHasherAdapter` does not exist.

- [ ] **Step 4: Write `PasswordHasherAdapter`**

Create `src/MarketPulse.Infrastructure/Authentication/PasswordHasherAdapter.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace MarketPulse.Infrastructure.Authentication;

/// <summary>
/// Wraps ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/> — PBKDF2-HMAC-SHA512,
/// 100,000 iterations, 128-bit random salt per hash. We reuse the audited implementation
/// but not the rest of Identity: <c>PasswordHasher&lt;T&gt;</c> is constrained to
/// <c>class</c>, not to <c>IdentityUser</c>, so no Identity type crosses into the domain.
/// </summary>
public sealed class PasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();

    /// <summary>
    /// Any non-null user instance works — <see cref="PasswordHasher{TUser}"/> ignores the
    /// argument entirely and salts randomly instead.
    /// </summary>
    private static readonly User Unused =
        new(Guid.Empty, "unused@marketpulse.local", string.Empty, DateTimeOffset.UnixEpoch);

    public string Hash(string password) => _inner.HashPassword(Unused, password);

    public bool Verify(string hash, string password)
    {
        try
        {
            var result = _inner.VerifyHashedPassword(Unused, hash, password);
            return result is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            // A hash that is not valid base64 — corrupt or hand-edited data.
            return false;
        }
    }
}
```

- [ ] **Step 5: Run the hasher tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~PasswordHasherAdapterTests`
Expected: 4 passed.

- [ ] **Step 6: Write the failing token service tests**

Create `tests/MarketPulse.UnitTests/Infrastructure/JwtTokenServiceTests.cs`:

```csharp
using System.IdentityModel.Tokens.Jwt;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace MarketPulse.UnitTests.Infrastructure;

public class JwtTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        SigningKey = "test-signing-key-that-is-long-enough-32",
        Issuer = "marketpulse-tests",
        Audience = "marketpulse-tests",
        AccessTokenLifetime = TimeSpan.FromMinutes(15)
    };

    private readonly JwtTokenService _service = new(Microsoft.Extensions.Options.Options.Create(Options));

    private static readonly User TestUser =
        new(Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "someone@marketpulse.local", "hash", DateTimeOffset.UnixEpoch);

    [Fact]
    public void The_access_token_carries_the_user_id_as_sub()
    {
        var token = _service.CreateAccessToken(TestUser);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(TestUser.Id.ToString(), jwt.Claims.Single(c => c.Type == "sub").Value);
    }

    [Fact]
    public void The_access_token_carries_the_issuer_audience_and_expiry()
    {
        var token = _service.CreateAccessToken(TestUser);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("marketpulse-tests", jwt.Issuer);
        Assert.Contains("marketpulse-tests", jwt.Audiences);
        Assert.True(jwt.ValidTo > DateTime.UtcNow.AddMinutes(14));
        Assert.True(jwt.ValidTo < DateTime.UtcNow.AddMinutes(16));
    }

    [Fact]
    public void Each_refresh_token_is_unique()
    {
        var (first, _) = _service.CreateRefreshToken();
        var (second, _) = _service.CreateRefreshToken();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_returned_hash_matches_hashing_the_returned_token()
    {
        var (token, hash) = _service.CreateRefreshToken();

        Assert.Equal(hash, _service.HashRefreshToken(token));
    }

    [Fact]
    public void The_refresh_token_is_not_its_own_hash()
    {
        var (token, hash) = _service.CreateRefreshToken();

        Assert.NotEqual(token, hash);
    }
}
```

- [ ] **Step 7: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~JwtTokenServiceTests`
Expected: build failure — `JwtTokenService` does not exist.

If `JwtSecurityTokenHandler` is also unresolved, add the reader package to the **test** project only: `dotnet add tests/MarketPulse.UnitTests package System.IdentityModel.Tokens.Jwt`, then re-check `Directory.Packages.props` as in Step 1.

- [ ] **Step 8: Write `JwtTokenService`**

Create `src/MarketPulse.Infrastructure/Authentication/JwtTokenService.cs`:

```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MarketPulse.Infrastructure.Authentication;

public sealed class JwtTokenService(IOptions<JwtOptions> options) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public string CreateAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = DateTime.UtcNow.Add(_options.AccessTokenLifetime),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ]),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>
    /// 256 bits from the CSPRNG, base64url-encoded so it survives a cookie value intact.
    /// </summary>
    public (string Token, string Hash) CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncoder.Encode(bytes);
        return (token, HashRefreshToken(token));
    }

    /// <summary>
    /// SHA-256, not PBKDF2. The input is 256 bits of cryptographic randomness rather than
    /// a low-entropy human secret, so there is no dictionary attack for a slow hash to
    /// defend against — and this runs on every authenticated refresh.
    /// </summary>
    public string HashRefreshToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
```

- [ ] **Step 9: Run the token service tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~JwtTokenServiceTests`
Expected: 5 passed.

- [ ] **Step 10: Commit**

```bash
git add src/MarketPulse.Infrastructure tests/MarketPulse.UnitTests/Infrastructure Directory.Packages.props tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj
git commit -m "feat: add password hashing and JWT token minting"
```

---

### Task 4: Infrastructure — persistence, migration, and DI

**Files:**
- Modify: `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`
- Modify: `src/MarketPulse.Infrastructure/Persistence/SeedData.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/UserRepository.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/RefreshTokenRepository.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/Migrations/*_AddAuthentication.cs` (generated)
- Test: `tests/MarketPulse.IntegrationTests/AuthPersistenceTests.cs`

**Interfaces:**
- Consumes: `User`, `RefreshToken` (Task 1); `IUserRepository`, `IRefreshTokenRepository` (Task 2); `PasswordHasherAdapter`, `JwtTokenService` (Task 3).
- Produces: `MarketPulseDbContext.RefreshTokens : DbSet<RefreshToken>`; `SeedData.DevUserPasswordHash : string`; `SeedData.DevUserPassword : string`; `AddInfrastructure` now registers `IUserRepository`, `IRefreshTokenRepository`, `IPasswordHasher`, `ITokenService`.

- [ ] **Step 1: Repair the integration test project**

`MarketPulse.IntegrationTests` has not compiled since Task 1 changed the `User` constructor, so nothing in this task can be verified until it does. Two files call it:

`tests/MarketPulse.IntegrationTests/WatchlistPersistenceTests.cs` — **lines 25 and 47**. Add the two new arguments, keeping each call's own variable name (`write` on line 25, `writeDb` on line 47) and the existing `userId`:

```csharp
            write.Users.Add(new User(userId, $"{userId}@test.local", "hash", DateTimeOffset.UtcNow));
```

`tests/MarketPulse.IntegrationTests/WatchlistApiTests.cs` — **line 40**, inside `InitializeAsync`:

```csharp
            db.Users.Add(new User(
                SeedData.DevUserId, SeedData.DevUserEmail,
                SeedData.DevUserPasswordHash, SeedData.DevUserCreatedUtc));
```

These are mechanical compile fixes only — do not restructure either test. Task 6 rewrites `WatchlistApiTests` properly once authentication exists.

Verify: `dotnet build tests/MarketPulse.IntegrationTests` succeeds.

- [ ] **Step 2: Map the new columns and the RefreshTokens table**

In `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`, add the DbSet beside the others:

```csharp
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
```

Replace the `b.Entity<User>` block with:

```csharp
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(x => x.CreatedUtc).IsRequired();
            e.Property(x => x.FailedLoginCount).IsRequired();
            e.Property(x => x.LockoutEndUtc);
            e.HasData(new User(
                SeedData.DevUserId,
                SeedData.DevUserEmail,
                SeedData.DevUserPasswordHash,
                SeedData.DevUserCreatedUtc));
        });
```

Add a new block after it:

```csharp
        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();

            // Every refresh looks a token up by hash — without this index that is a
            // table scan on the hottest authenticated path in the app.
            e.HasIndex(x => x.TokenHash).IsUnique();

            // Reuse detection revokes the whole family, which queries by user.
            e.HasIndex(x => x.UserId);

            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 3: Add the repositories**

Create `src/MarketPulse.Infrastructure/Persistence/UserRepository.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class UserRepository(MarketPulseDbContext db) : IUserRepository
{
    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == User.Normalise(email), ct);

    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Email == User.Normalise(email), ct);

    public async Task AddAsync(User user, CancellationToken ct) =>
        await db.Users.AddAsync(user, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
```

If EF cannot translate `User.Normalise(email)` into SQL, normalise before the query instead — assign `var normalised = User.Normalise(email);` and compare against that local. Verify against the tests in Step 8 rather than assuming.

Create `src/MarketPulse.Infrastructure/Persistence/RefreshTokenRepository.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class RefreshTokenRepository(MarketPulseDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task AddAsync(RefreshToken token, CancellationToken ct) =>
        await db.RefreshTokens.AddAsync(token, ct);

    public async Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedUtc == null)
            .ToListAsync(ct);

        foreach (var token in live)
        {
            token.Revoke(now);
        }
    }

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
```

- [ ] **Step 4: Register everything in DI**

Replace the body of `AddInfrastructure` in `src/MarketPulse.Infrastructure/DependencyInjection.cs`:

```csharp
        services.AddDbContext<MarketPulseDbContext>(o => o.UseSqlServer(connectionString));
        services.AddScoped<IWatchlistRepository, WatchlistRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<PriceTickChannel>();
        services.AddHostedService<FakeTickService>();
        return services;
```

Add `using MarketPulse.Infrastructure.Authentication;` at the top.

- [ ] **Step 5: Generate the migration**

```bash
export MARKETPULSE_SA_PASSWORD='Local!Dev!Pass123'
dotnet ef migrations add AddAuthentication \
  --project src/MarketPulse.Infrastructure \
  --startup-project src/MarketPulse.Api
```

Read the generated migration and confirm it: adds `PasswordHash`, `CreatedUtc`, `FailedLoginCount`, `LockoutEndUtc` to `Users`; creates `RefreshTokens` with both indexes; and issues an `UpdateData` for the seeded dev user's new columns. It must **not** touch `Watchlists` or `WatchlistItems` — if it does, something in Task 1 changed the watchlist model and must be reverted.

- [ ] **Step 6: Write the failing persistence tests**

Create `tests/MarketPulse.IntegrationTests/AuthPersistenceTests.cs`:

```csharp
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class AuthPersistenceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task The_seeded_dev_user_has_a_usable_password_hash()
    {
        await using var db = fixture.CreateContext();

        var dev = await db.Users.SingleAsync(u => u.Id == SeedData.DevUserId);

        Assert.Equal(SeedData.DevUserEmail, dev.Email);
        Assert.False(string.IsNullOrWhiteSpace(dev.PasswordHash));

        var hasher = new Infrastructure.Authentication.PasswordHasherAdapter();
        Assert.True(hasher.Verify(dev.PasswordHash, SeedData.DevUserPassword));
    }

    [Fact]
    public async Task A_user_round_trips_through_the_repository()
    {
        var email = $"user-{Guid.NewGuid():N}@marketpulse.local";

        await using (var write = fixture.CreateContext())
        {
            var repo = new UserRepository(write);
            await repo.AddAsync(User.Register(email, "hashed"), CancellationToken.None);
            await repo.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = fixture.CreateContext();
        var found = await new UserRepository(read)
            .GetByEmailAsync(email.ToUpperInvariant(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(email, found!.Email);
    }

    [Fact]
    public async Task Revoking_a_family_revokes_every_live_token_for_that_user_only()
    {
        var mine = User.Register($"mine-{Guid.NewGuid():N}@marketpulse.local", "h");
        var theirs = User.Register($"theirs-{Guid.NewGuid():N}@marketpulse.local", "h");
        var now = DateTimeOffset.UtcNow;

        await using (var write = fixture.CreateContext())
        {
            write.Users.AddRange(mine, theirs);
            write.RefreshTokens.AddRange(
                RefreshToken.Issue(mine.Id, $"a-{Guid.NewGuid():N}", now, TimeSpan.FromDays(14)),
                RefreshToken.Issue(mine.Id, $"b-{Guid.NewGuid():N}", now, TimeSpan.FromDays(14)),
                RefreshToken.Issue(theirs.Id, $"c-{Guid.NewGuid():N}", now, TimeSpan.FromDays(14)));
            await write.SaveChangesAsync();
        }

        await using (var act = fixture.CreateContext())
        {
            var repo = new RefreshTokenRepository(act);
            await repo.RevokeAllForUserAsync(mine.Id, now, CancellationToken.None);
            await repo.SaveChangesAsync(CancellationToken.None);
        }

        await using var assert = fixture.CreateContext();
        Assert.Empty(assert.RefreshTokens.Where(t => t.UserId == mine.Id && t.RevokedUtc == null));
        Assert.Single(assert.RefreshTokens.Where(t => t.UserId == theirs.Id && t.RevokedUtc == null));
    }
}
```

- [ ] **Step 7: Run the integration tests**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~AuthPersistenceTests`
Expected: 3 passed. Testcontainers starts a real SQL Server and applies the new migration.

If `GetByEmailAsync` throws a translation error, apply the fallback described in Step 3 and re-run.

- [ ] **Step 8: Commit**

```bash
git add src/MarketPulse.Infrastructure tests/MarketPulse.IntegrationTests/AuthPersistenceTests.cs
git commit -m "feat: persist credentials and refresh tokens"
```

---

### Task 5: Application — the four authentication use cases

**Files:**
- Create: `src/MarketPulse.Application/Authentication/RegisterUserCommand.cs`
- Create: `src/MarketPulse.Application/Authentication/LoginCommand.cs`
- Create: `src/MarketPulse.Application/Authentication/RefreshSessionCommand.cs`
- Create: `src/MarketPulse.Application/Authentication/LogoutCommand.cs`
- Test: `tests/MarketPulse.UnitTests/Application/LoginHandlerTests.cs`
- Test: `tests/MarketPulse.UnitTests/Application/RefreshSessionHandlerTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–2. `IWatchlistRepository` (slice 1, unchanged) for the registration watchlist.
- Produces:
  - `RegisterUserCommand(string Email, string Password) : IRequest<AuthResult>`
  - `LoginCommand(string Email, string Password) : IRequest<AuthResult>`
  - `RefreshSessionCommand(string RefreshToken) : IRequest<AuthResult>`
  - `LogoutCommand(string? RefreshToken) : IRequest<Unit>`

- [ ] **Step 1: Write `RegisterUserCommand`**

Create `src/MarketPulse.Application/Authentication/RegisterUserCommand.cs`:

```csharp
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.Authentication;

public record RegisterUserCommand(string Email, string Password) : IRequest<AuthResult>;

public sealed class RegisterUserValidator : AbstractValidator<RegisterUserCommand>
{
    public RegisterUserValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.").WithErrorCode("invalid-email")
            .MaximumLength(256).WithMessage("Email must be 256 characters or fewer.").WithErrorCode("invalid-email")
            .EmailAddress().WithMessage("Enter a valid email address.").WithErrorCode("invalid-email");

        RuleFor(x => x.Password)
            .Must(PasswordPolicy.IsAcceptable)
            .WithMessage($"Password must be at least {PasswordPolicy.MinimumLength} characters " +
                         "and must not be a commonly used password.")
            .WithErrorCode("weak-password");
    }
}

public sealed class RegisterUserHandler(
    IUserRepository users,
    IWatchlistRepository watchlists,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    IOptions<JwtOptions> jwt)
    : IRequestHandler<RegisterUserCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RegisterUserCommand request, CancellationToken ct)
    {
        if (await users.EmailExistsAsync(request.Email, ct))
        {
            throw new EmailTakenException();
        }

        var user = User.Register(request.Email, hasher.Hash(request.Password));
        await users.AddAsync(user, ct);

        // Give the new account a watchlist up front so the dashboard has something to
        // read on first load.
        await watchlists.AddAsync(Watchlist.Create(user.Id), ct);
        await users.SaveChangesAsync(ct);

        return await SessionFactory.IssueAsync(user, refreshTokens, tokens, jwt.Value, ct);
    }
}
```

- [ ] **Step 2: Write the shared session factory**

Registration, login and refresh all mint the same pair of tokens. Add it to the same folder — create `src/MarketPulse.Application/Authentication/SessionFactory.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Authentication;

/// <summary>
/// The one place a session is minted. Registration, login and refresh all land here so
/// the token pair can never drift apart between entry points.
/// </summary>
internal static class SessionFactory
{
    public static async Task<AuthResult> IssueAsync(
        User user,
        IRefreshTokenRepository refreshTokens,
        ITokenService tokens,
        JwtOptions jwt,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (token, hash) = tokens.CreateRefreshToken();

        var refresh = RefreshToken.Issue(user.Id, hash, now, jwt.RefreshTokenLifetime);
        await refreshTokens.AddAsync(refresh, ct);
        await refreshTokens.SaveChangesAsync(ct);

        return new AuthResult(
            user.Id,
            user.Email,
            tokens.CreateAccessToken(user),
            now.Add(jwt.AccessTokenLifetime),
            token,
            refresh.ExpiresUtc);
    }
}
```

- [ ] **Step 3: Write the failing login tests**

Create `tests/MarketPulse.UnitTests/Application/LoginHandlerTests.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Authentication;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class LoginHandlerTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();

    private static readonly AuthOptions Auth = new()
    {
        MaxFailedAttempts = 5,
        LockoutDuration = TimeSpan.FromMinutes(15)
    };

    private static readonly JwtOptions Jwt = new()
    {
        SigningKey = "test-signing-key-that-is-long-enough-32",
        Issuer = "t",
        Audience = "t"
    };

    private LoginHandler CreateHandler() => new(
        _users, _refreshTokens, _hasher, _tokens,
        Options.Create(Jwt), Options.Create(Auth));

    private static User AUser() =>
        User.Register("someone@marketpulse.local", "stored-hash");

    public LoginHandlerTests()
    {
        _tokens.CreateRefreshToken().Returns(("refresh-token", "refresh-hash"));
        _tokens.CreateAccessToken(Arg.Any<User>()).Returns("access-token");
    }

    [Fact]
    public async Task An_unknown_email_is_rejected_as_invalid_credentials()
    {
        _users.GetByEmailAsync("nobody@marketpulse.local", Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            CreateHandler().Handle(
                new LoginCommand("nobody@marketpulse.local", "whatever12345"),
                CancellationToken.None));
    }

    [Fact]
    public async Task An_unknown_email_still_runs_a_hash_verification()
    {
        // Timing equalisation: returning early without hashing makes "unknown email"
        // measurably faster than "wrong password", which is an enumeration oracle.
        _users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            CreateHandler().Handle(
                new LoginCommand("nobody@marketpulse.local", "whatever12345"),
                CancellationToken.None));

        _hasher.Received().Verify(Arg.Any<string>(), "whatever12345");
    }

    [Fact]
    public async Task A_wrong_password_increments_the_failure_count()
    {
        var user = AUser();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("stored-hash", "wrong-password").Returns(false);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            CreateHandler().Handle(
                new LoginCommand(user.Email, "wrong-password"), CancellationToken.None));

        Assert.Equal(1, user.FailedLoginCount);
        await _users.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_locked_account_is_rejected_before_the_password_is_checked()
    {
        var user = AUser();
        for (var i = 0; i < 5; i++)
        {
            user.RecordFailedLogin(DateTimeOffset.UtcNow, 5, TimeSpan.FromMinutes(15));
        }

        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);

        await Assert.ThrowsAsync<AccountLockedException>(() =>
            CreateHandler().Handle(
                new LoginCommand(user.Email, "correct-password"), CancellationToken.None));

        _hasher.DidNotReceive().Verify("stored-hash", "correct-password");
    }

    [Fact]
    public async Task A_correct_password_returns_a_session_and_resets_the_counter()
    {
        var user = AUser();
        user.RecordFailedLogin(DateTimeOffset.UtcNow, 5, TimeSpan.FromMinutes(15));

        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("stored-hash", "right-password").Returns(true);

        var result = await CreateHandler().Handle(
            new LoginCommand(user.Email, "right-password"), CancellationToken.None);

        Assert.Equal("access-token", result.AccessToken);
        Assert.Equal("refresh-token", result.RefreshToken);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(0, user.FailedLoginCount);
    }
}
```

- [ ] **Step 4: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~LoginHandlerTests`
Expected: build failure — `LoginCommand` and `LoginHandler` do not exist.

- [ ] **Step 5: Write `LoginCommand`**

First generate the timing-equalisation hash, the same way Task 4 Step 1 generated the seed hash — add a throwaway test, read the value off the failure, then delete it:

```csharp
// tests/MarketPulse.UnitTests/Infrastructure/DummyHashGen.cs  (DELETE after this step)
using MarketPulse.Infrastructure.Authentication;

namespace MarketPulse.UnitTests.Infrastructure;

public class DummyHashGen
{
    [Fact]
    public void Print() => Assert.Fail(
        new PasswordHasherAdapter().Hash($"timing-equalisation-{Guid.NewGuid()}"));
}
```

Run `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~DummyHashGen`, copy the `AQAAAA…` value into `DummyHash` below, and delete the file. The plaintext is deliberately random and discarded — nothing should ever verify against it successfully.

Create `src/MarketPulse.Application/Authentication/LoginCommand.cs`:

```csharp
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.Authentication;

public record LoginCommand(string Email, string Password) : IRequest<AuthResult>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        // Shape only. The password policy is deliberately NOT applied here — an existing
        // account whose password predates a policy change must still be able to sign in.
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.").WithErrorCode("invalid-email");
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.").WithErrorCode("invalid-password");
    }
}

public sealed class LoginHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher hasher,
    ITokenService tokens,
    IOptions<JwtOptions> jwt,
    IOptions<AuthOptions> auth)
    : IRequestHandler<LoginCommand, AuthResult>
{
    /// <summary>
    /// A real PBKDF2 hash of a value nobody knows. Verifying against it when the email is
    /// unknown keeps the response time of "no such user" indistinguishable from
    /// "wrong password". It must be a genuinely well-formed hash — a made-up string would
    /// fail format parsing and return in microseconds, which is the exact timing signal
    /// this is meant to remove.
    /// </summary>
    private const string DummyHash = "PASTE_A_GENERATED_HASH_HERE";

    public async Task<AuthResult> Handle(LoginCommand request, CancellationToken ct)
    {
        var options = auth.Value;
        var now = DateTimeOffset.UtcNow;
        var user = await users.GetByEmailAsync(request.Email, ct);

        if (user is null)
        {
            hasher.Verify(DummyHash, request.Password);
            throw new InvalidCredentialsException();
        }

        if (user.IsLockedOut(now))
        {
            throw new AccountLockedException(user.LockoutEndUtc!.Value - now);
        }

        if (!hasher.Verify(user.PasswordHash, request.Password))
        {
            user.RecordFailedLogin(now, options.MaxFailedAttempts, options.LockoutDuration);
            await users.SaveChangesAsync(ct);
            throw new InvalidCredentialsException();
        }

        user.RecordSuccessfulLogin();
        await users.SaveChangesAsync(ct);

        return await SessionFactory.IssueAsync(user, refreshTokens, tokens, jwt.Value, ct);
    }
}
```

- [ ] **Step 6: Run the login tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~LoginHandlerTests`
Expected: 5 passed.

- [ ] **Step 7: Write the failing refresh tests**

Create `tests/MarketPulse.UnitTests/Application/RefreshSessionHandlerTests.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Authentication;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class RefreshSessionHandlerTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();

    private static readonly JwtOptions Jwt = new()
    {
        SigningKey = "test-signing-key-that-is-long-enough-32",
        Issuer = "t",
        Audience = "t",
        RefreshTokenLifetime = TimeSpan.FromDays(14)
    };

    private readonly User _user = User.Register("someone@marketpulse.local", "hash");

    private RefreshSessionHandler CreateHandler() =>
        new(_users, _refreshTokens, _tokens, Options.Create(Jwt));

    public RefreshSessionHandlerTests()
    {
        _tokens.HashRefreshToken("presented").Returns("presented-hash");
        _tokens.CreateRefreshToken().Returns(("new-token", "new-hash"));
        _tokens.CreateAccessToken(Arg.Any<User>()).Returns("access-token");
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>())
            .Returns((RefreshToken?)null);

        await Assert.ThrowsAsync<SessionRevokedException>(() =>
            CreateHandler().Handle(new RefreshSessionCommand("presented"), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var expired = RefreshToken.Issue(
            _user.Id, "presented-hash", DateTimeOffset.UtcNow.AddDays(-20), TimeSpan.FromDays(14));
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(expired);

        await Assert.ThrowsAsync<SessionRevokedException>(() =>
            CreateHandler().Handle(new RefreshSessionCommand("presented"), CancellationToken.None));
    }

    [Fact]
    public async Task Replaying_a_revoked_token_revokes_the_whole_family()
    {
        var revoked = RefreshToken.Issue(
            _user.Id, "presented-hash", DateTimeOffset.UtcNow, TimeSpan.FromDays(14));
        revoked.Revoke(DateTimeOffset.UtcNow);
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(revoked);

        await Assert.ThrowsAsync<SessionRevokedException>(() =>
            CreateHandler().Handle(new RefreshSessionCommand("presented"), CancellationToken.None));

        await _refreshTokens.Received().RevokeAllForUserAsync(
            _user.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_valid_token_rotates_and_records_its_successor()
    {
        var live = RefreshToken.Issue(
            _user.Id, "presented-hash", DateTimeOffset.UtcNow, TimeSpan.FromDays(14));
        _refreshTokens.GetByHashAsync("presented-hash", Arg.Any<CancellationToken>()).Returns(live);

        var result = await CreateHandler().Handle(
            new RefreshSessionCommand("presented"), CancellationToken.None);

        Assert.Equal("new-token", result.RefreshToken);
        Assert.Equal("access-token", result.AccessToken);
        Assert.False(live.IsActive(DateTimeOffset.UtcNow));
        Assert.NotNull(live.ReplacedByTokenId);
    }
}
```

- [ ] **Step 8: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~RefreshSessionHandlerTests`
Expected: build failure — `RefreshSessionCommand` does not exist.

- [ ] **Step 9: Write `RefreshSessionCommand`**

Create `src/MarketPulse.Application/Authentication/RefreshSessionCommand.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.Authentication;

public record RefreshSessionCommand(string RefreshToken) : IRequest<AuthResult>;

public sealed class RefreshSessionHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens,
    IOptions<JwtOptions> jwt)
    : IRequestHandler<RefreshSessionCommand, AuthResult>
{
    public async Task<AuthResult> Handle(RefreshSessionCommand request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var hash = tokens.HashRefreshToken(request.RefreshToken);
        var existing = await refreshTokens.GetByHashAsync(hash, ct);

        if (existing is null)
        {
            throw new SessionRevokedException();
        }

        // A token we issued, already revoked, presented again: it leaked. Whoever holds
        // it and whoever holds its successor are indistinguishable from here, so end
        // every live session this user has and make them sign in again.
        if (existing.RevokedUtc is not null)
        {
            await refreshTokens.RevokeAllForUserAsync(existing.UserId, now, ct);
            await refreshTokens.SaveChangesAsync(ct);
            throw new SessionRevokedException();
        }

        if (!existing.IsActive(now))
        {
            throw new SessionRevokedException();
        }

        var user = await users.GetByIdAsync(existing.UserId, ct)
            ?? throw new SessionRevokedException();

        var (token, newHash) = tokens.CreateRefreshToken();
        var replacement = RefreshToken.Issue(user.Id, newHash, now, jwt.Value.RefreshTokenLifetime);

        existing.ReplaceWith(replacement.Id, now);
        await refreshTokens.AddAsync(replacement, ct);
        await refreshTokens.SaveChangesAsync(ct);

        return new AuthResult(
            user.Id,
            user.Email,
            tokens.CreateAccessToken(user),
            now.Add(jwt.Value.AccessTokenLifetime),
            token,
            replacement.ExpiresUtc);
    }
}
```

- [ ] **Step 10: Run the refresh tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~RefreshSessionHandlerTests`
Expected: 4 passed.

- [ ] **Step 11: Write `LogoutCommand`**

Create `src/MarketPulse.Application/Authentication/LogoutCommand.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Authentication;

public record LogoutCommand(string? RefreshToken) : IRequest<Unit>;

public sealed class LogoutHandler(
    IRefreshTokenRepository refreshTokens,
    ITokenService tokens)
    : IRequestHandler<LogoutCommand, Unit>
{
    /// <summary>
    /// Deliberately forgiving: logging out with a missing or already-dead token is a
    /// success, not an error. The API layer clears the cookies either way.
    /// </summary>
    public async Task<Unit> Handle(LogoutCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return Unit.Value;
        }

        var existing = await refreshTokens.GetByHashAsync(
            tokens.HashRefreshToken(request.RefreshToken), ct);

        if (existing is not null)
        {
            await refreshTokens.RevokeAllForUserAsync(existing.UserId, DateTimeOffset.UtcNow, ct);
            await refreshTokens.SaveChangesAsync(ct);
        }

        return Unit.Value;
    }
}
```

- [ ] **Step 12: Run the whole unit suite**

Run: `dotnet test tests/MarketPulse.UnitTests`
Expected: all passing. The slice 1 tests (`WatchlistTests`, `RandomWalkTests`, `AddWatchlistItemHandlerTests`, `DependencyRuleTests`) must be untouched and green.

- [ ] **Step 13: Commit**

```bash
git add src/MarketPulse.Application/Authentication tests/MarketPulse.UnitTests/Application
git commit -m "feat: add register, login, refresh, and logout handlers"
```

---

### Task 6: Api — the swap

This is the atomic task. It deletes `DevAuthMiddleware`, turns on `[Authorize]`, and repairs the slice 1 integration tests in one commit, because any split leaves the build red.

**Files:**
- Create: `src/MarketPulse.Api/Authentication/AuthCookies.cs`
- Create: `src/MarketPulse.Api/Controllers/AuthController.cs`
- Delete: `src/MarketPulse.Api/Middleware/DevAuthMiddleware.cs`
- Modify: `src/MarketPulse.Api/Program.cs`
- Modify: `src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs`
- Modify: `src/MarketPulse.Api/Controllers/WatchlistController.cs`
- Modify: `src/MarketPulse.Api/Hubs/PriceHub.cs`
- Modify: `src/MarketPulse.Api/appsettings.json`, `appsettings.Development.json`
- Modify: `src/MarketPulse.Api/MarketPulse.Api.csproj`, `Directory.Packages.props`
- Create: `tests/MarketPulse.IntegrationTests/AuthenticatedClient.cs`
- Create: `tests/MarketPulse.IntegrationTests/AuthApiTests.cs`
- Modify: `tests/MarketPulse.IntegrationTests/WatchlistApiTests.cs`, `PriceStreamTests.cs`, `WatchlistPersistenceTests.cs`
- Test: `tests/MarketPulse.UnitTests/Api/AuthCookiesTests.cs`

**Interfaces:**
- Consumes: `AuthResult` and the four commands (Task 5); `JwtOptions`, `AuthOptions` (Task 2).
- Produces:
  - `AuthCookies.Access`/`.Refresh`/`.Csrf` — the three cookie names
  - `AuthCookies.RefreshPath` = `"/api/v1/auth/refresh"`
  - `AuthCookies.Build(bool isDevelopment, SameSiteMode sameSite, DateTimeOffset expires, string? path) : CookieOptions`
  - `POST /api/v1/auth/{register,login,refresh,logout}`, `GET /api/v1/auth/me`, `GET /health`
  - `SessionResponse(Guid Id, string Email)` — the `/me` body
  - `AuthenticatedClient.RegisterAsync(WebApplicationFactory<Program>, string email, string password)` for the test suite

- [ ] **Step 1: Add the JwtBearer package**

```bash
dotnet add src/MarketPulse.Api package Microsoft.AspNetCore.Authentication.JwtBearer --version 10.0.10
```

Then verify `Directory.Packages.props` (concrete version, trailing newline) as in Task 3 Step 1.

- [ ] **Step 2: Write the failing cookie-options tests**

Create `tests/MarketPulse.UnitTests/Api/AuthCookiesTests.cs`:

```csharp
using MarketPulse.Api.Authentication;
using Microsoft.AspNetCore.Http;

namespace MarketPulse.UnitTests.Api;

public class AuthCookiesTests
{
    private static readonly DateTimeOffset Expires = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Outside_development_cookies_are_secure()
    {
        var options = AuthCookies.Build(isDevelopment: false, SameSiteMode.Lax, Expires, path: null);

        Assert.True(options.Secure);
    }

    [Fact]
    public void In_development_cookies_are_not_secure()
    {
        // .NET's CookieContainer refuses to send Secure cookies over plain HTTP, which
        // would break every integration test and the local dev loop.
        var options = AuthCookies.Build(isDevelopment: true, SameSiteMode.Lax, Expires, path: null);

        Assert.False(options.Secure);
    }

    [Fact]
    public void Cookies_are_http_only_by_default()
    {
        var options = AuthCookies.Build(isDevelopment: true, SameSiteMode.Lax, Expires, path: null);

        Assert.True(options.HttpOnly);
    }

    [Fact]
    public void The_path_is_applied_when_supplied()
    {
        var options = AuthCookies.Build(
            isDevelopment: true, SameSiteMode.Strict, Expires, AuthCookies.RefreshPath);

        Assert.Equal("/api/v1/auth/refresh", options.Path);
        Assert.Equal(SameSiteMode.Strict, options.SameSite);
    }

    [Fact]
    public void The_refresh_cookie_is_scoped_to_the_refresh_endpoint()
    {
        Assert.Equal("/api/v1/auth/refresh", AuthCookies.RefreshPath);
    }
}
```

- [ ] **Step 3: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~AuthCookiesTests`
Expected: build failure — `AuthCookies` does not exist. If the test project cannot see `Microsoft.AspNetCore.Http`, add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to an `ItemGroup` in `tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj`, and add a `ProjectReference` to `src/MarketPulse.Api/MarketPulse.Api.csproj`.

- [ ] **Step 4: Write `AuthCookies`**

Create `src/MarketPulse.Api/Authentication/AuthCookies.cs`:

```csharp
namespace MarketPulse.Api.Authentication;

/// <summary>
/// Cookie names and the one place their security attributes are decided. Kept as a pure
/// function so the attributes are unit-testable without spinning up a host.
/// </summary>
public static class AuthCookies
{
    public const string Access = "mp_access";
    public const string Refresh = "mp_refresh";
    public const string Csrf = "mp_csrf";

    /// <summary>
    /// The refresh cookie is scoped to the one endpoint that consumes it, so it is not
    /// transmitted on any other request.
    /// </summary>
    public const string RefreshPath = "/api/v1/auth/refresh";

    public static CookieOptions Build(
        bool isDevelopment,
        SameSiteMode sameSite,
        DateTimeOffset expires,
        string? path) => new()
    {
        HttpOnly = true,

        // Secure is off in Development on purpose: localhost is served over plain HTTP,
        // and .NET's CookieContainer will not send a Secure cookie over HTTP, which would
        // silently break the integration suite.
        Secure = !isDevelopment,
        SameSite = sameSite,
        Expires = expires,
        Path = path ?? "/"
    };
}
```

- [ ] **Step 5: Run the cookie tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~AuthCookiesTests`
Expected: 5 passed.

- [ ] **Step 6: Write `AuthController`**

Create `src/MarketPulse.Api/Controllers/AuthController.cs`:

```csharp
using System.Security.Cryptography;
using MarketPulse.Api.Authentication;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Authentication;
using MarketPulse.Application.Configuration;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MarketPulse.Api.Controllers;

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record SessionResponse(Guid Id, string Email);

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    ISender sender,
    IHostEnvironment environment) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<SessionResponse>> Register(
        [FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new RegisterUserCommand(request.Email, request.Password), ct);
        return IssueSession(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<SessionResponse>> Login(
        [FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new LoginCommand(request.Email, request.Password), ct);
        return IssueSession(result);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<SessionResponse>> Refresh(CancellationToken ct)
    {
        var token = Request.Cookies[AuthCookies.Refresh] ?? string.Empty;
        var result = await sender.Send(new RefreshSessionCommand(token), ct);
        return IssueSession(result);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await sender.Send(new LogoutCommand(Request.Cookies[AuthCookies.Refresh]), ct);
        ClearSession();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<SessionResponse> Me()
    {
        var id = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;

        return Guid.TryParse(id, out var userId)
            ? Ok(new SessionResponse(userId, email))
            : Unauthorized();
    }

    private ActionResult<SessionResponse> IssueSession(AuthResult result)
    {
        var isDev = environment.IsDevelopment();

        Response.Cookies.Append(
            AuthCookies.Access, result.AccessToken,
            AuthCookies.Build(isDev, SameSiteMode.Lax, result.AccessExpiresUtc, path: null));

        Response.Cookies.Append(
            AuthCookies.Refresh, result.RefreshToken,
            AuthCookies.Build(isDev, SameSiteMode.Strict, result.RefreshExpiresUtc,
                AuthCookies.RefreshPath));

        // The CSRF nonce must outlive the access token, or a client whose access token has
        // expired would have no way to authenticate its own refresh call. It tracks the
        // refresh lifetime instead, and is readable by JavaScript by design — that is what
        // "double submit" means.
        var csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Response.Cookies.Append(AuthCookies.Csrf, csrf, new CookieOptions
        {
            HttpOnly = false,
            Secure = !isDev,
            SameSite = SameSiteMode.Lax,
            Expires = result.RefreshExpiresUtc,
            Path = "/"
        });

        return Ok(new SessionResponse(result.UserId, result.Email));
    }

    private void ClearSession()
    {
        var isDev = environment.IsDevelopment();
        var expired = DateTimeOffset.UnixEpoch;

        Response.Cookies.Append(AuthCookies.Access, string.Empty,
            AuthCookies.Build(isDev, SameSiteMode.Lax, expired, path: null));
        Response.Cookies.Append(AuthCookies.Refresh, string.Empty,
            AuthCookies.Build(isDev, SameSiteMode.Strict, expired, AuthCookies.RefreshPath));
        Response.Cookies.Append(AuthCookies.Csrf, string.Empty, new CookieOptions
        {
            HttpOnly = false,
            Secure = !isDev,
            SameSite = SameSiteMode.Lax,
            Expires = expired,
            Path = "/"
        });
    }
}
```

Note the imports: `MarketPulse.Application.Configuration` and `Microsoft.Extensions.Options` are only needed if you add options injection later — drop any `using` the compiler reports as unnecessary, since `TreatWarningsAsErrors` turns an unread primary-constructor parameter (CS9113) and unused usings into build errors.

- [ ] **Step 7: Map exceptions to their own status codes**

In `src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs`, replace the `catch (DomainException ex)` block and add a `catch` for unauthenticated access. The `DomainException` catch becomes:

```csharp
        catch (DomainException ex)
        {
            if (ex is AccountLockedException locked && !context.Response.HasStarted)
            {
                context.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(locked.RetryAfter.TotalSeconds)).ToString();
            }

            await WriteAsync(context, ex.StatusCode, ex.ErrorCode, ex.Message);
        }
        catch (UnauthorizedAccessException)
        {
            // ICurrentUser throws this when no authenticated principal is on the request.
            // Slice 1 let it fall through to a 500; it is a 401.
            await WriteAsync(context, StatusCodes.Status401Unauthorized,
                "unauthenticated", "Authentication is required.");
        }
```

The `catch (ValidationException ex)` and `catch (Exception ex)` blocks stay exactly as they are. `catch (UnauthorizedAccessException)` must sit **above** `catch (Exception ex)`.

- [ ] **Step 8: Protect the watchlist and the hub**

In `src/MarketPulse.Api/Controllers/WatchlistController.cs`, add `using Microsoft.AspNetCore.Authorization;` and put `[Authorize]` on the class, immediately below `[ApiController]`. Change nothing else.

Replace `src/MarketPulse.Api/Hubs/PriceHub.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.Hubs;

/// <summary>
/// Prices are public data, so every authenticated client gets the same broadcast —
/// but an anonymous connection is refused. The cookie rides the negotiate request and
/// the websocket handshake, so no query-string access token is needed.
/// </summary>
[Authorize]
public sealed class PriceHub : Hub;
```

- [ ] **Step 9: Delete the stub**

```bash
git rm src/MarketPulse.Api/Middleware/DevAuthMiddleware.cs
```

- [ ] **Step 10: Rewrite `Program.cs`**

Replace `src/MarketPulse.Api/Program.cs`:

```csharp
using System.Text;
using System.Threading.RateLimiting;
using MarketPulse.Api;
using MarketPulse.Api.Authentication;
using MarketPulse.Api.Hubs;
using MarketPulse.Api.Middleware;
using MarketPulse.Api.RealTime;
using MarketPulse.Application;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MarketPulse")
    ?? throw new InvalidOperationException("ConnectionStrings:MarketPulse is not configured.");

// Options pattern with startup validation: a missing or too-short signing key fails the
// process at boot rather than at the first login attempt.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("The Jwt configuration section is missing.");

var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
    ?? new AuthOptions();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // `sub` is mapped to ClaimTypes.NameIdentifier, which is what ICurrentUser reads.
        // Set explicitly rather than relying on the framework default.
        options.MapInboundClaims = true;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),

            // No grace period. A 15-minute token must stop working at 15 minutes, or the
            // silent-refresh test is measuring the default five-minute skew instead.
            ClockSkew = TimeSpan.Zero
        };

        // The token lives in an httpOnly cookie, not an Authorization header. This also
        // covers SignalR: cookies ride both the negotiate request and the websocket
        // handshake, so the hub needs no query-string token.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue(AuthCookies.Access, out var token))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = auth.LoginRequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();
builder.Services.AddHostedService<TickBroadcaster>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(origins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<PriceHub>("/hubs/prices");
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

public partial class Program;
```

CSRF is not wired here — `CsrfMiddleware` does not exist until Task 7, which inserts its registration between `UseRateLimiter()` and `UseAuthentication()`. Do not leave a commented-out placeholder for it.

- [ ] **Step 11: Add the configuration**

In `src/MarketPulse.Api/appsettings.json`, add alongside the existing keys:

```json
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDuration": "00:15:00",
    "LoginRequestsPerMinute": 10
  },
  "Cors": {
    "AllowedOrigins": [ "http://localhost:5173", "http://localhost:4173" ]
  }
```

In `src/MarketPulse.Api/appsettings.Development.json`, add the development signing key. Slice 1 already set the precedent of dev-only secrets in this file:

```json
  "Jwt": {
    "SigningKey": "dev-only-signing-key-not-for-production-use-32+",
    "Issuer": "marketpulse-dev",
    "Audience": "marketpulse-dev",
    "AccessTokenLifetime": "00:15:00",
    "RefreshTokenLifetime": "14.00:00:00"
  }
```

Production supplies `Jwt__SigningKey` as an environment variable; `ValidateOnStart` fails the boot if it is absent.

- [ ] **Step 12: Add the test authentication helper**

Create `tests/MarketPulse.IntegrationTests/AuthenticatedClient.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// Registers a fresh user and returns a client whose cookie jar holds that session,
/// with the CSRF header pre-attached so mutations pass the double-submit check.
/// </summary>
public static class AuthenticatedClient
{
    public const string ValidPassword = "correct horse battery staple";

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@marketpulse.local";

    public static async Task<HttpClient> RegisterAsync(
        WebApplicationFactory<Program> factory, string? email = null)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { Email = email ?? NewEmail(), Password = ValidPassword });

        response.EnsureSuccessStatusCode();
        AttachCsrfHeader(client, response);
        return client;
    }

    public static async Task<HttpClient> LoginAsync(
        WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new { Email = email, Password = password });

        response.EnsureSuccessStatusCode();
        AttachCsrfHeader(client, response);
        return client;
    }

    /// <summary>
    /// Reads the mp_csrf value out of the Set-Cookie headers and sets it as the
    /// X-CSRF-Token header — exactly what the browser client does in JavaScript.
    /// </summary>
    public static void AttachCsrfHeader(HttpClient client, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return;
        }

        var csrf = cookies
            .Select(c => c.Split(';')[0])
            .FirstOrDefault(c => c.StartsWith("mp_csrf=", StringComparison.Ordinal))
            ?["mp_csrf=".Length..];

        if (!string.IsNullOrEmpty(csrf))
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-Token");
            client.DefaultRequestHeaders.Add("X-CSRF-Token", Uri.UnescapeDataString(csrf));
        }
    }
}
```

- [ ] **Step 13: Write the failing auth API tests**

Create `tests/MarketPulse.IntegrationTests/AuthApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using MarketPulse.Api.Controllers;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class AuthApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);
            b.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(DbContextOptions<MarketPulseDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Registering_sets_all_three_cookies()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = AuthenticatedClient.ValidPassword });

        response.EnsureSuccessStatusCode();
        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        Assert.Contains(cookies, c => c.StartsWith("mp_access=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("mp_refresh=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("mp_csrf=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_access_and_refresh_cookies_are_http_only_and_the_csrf_cookie_is_not()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = AuthenticatedClient.ValidPassword });

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();

        Assert.Contains(cookies, c => c.StartsWith("mp_access=", StringComparison.Ordinal)
                                      && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.StartsWith("mp_refresh=", StringComparison.Ordinal)
                                      && c.Contains("path=/api/v1/auth/refresh", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.StartsWith("mp_csrf=", StringComparison.Ordinal)
                                      && !c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_weak_password_is_rejected_with_400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = AuthenticatedClient.NewEmail(), Password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Registering_the_same_email_twice_returns_409()
    {
        var client = _factory.CreateClient();
        var email = AuthenticatedClient.NewEmail();

        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        var second = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("email-taken", body!["title"].ToString());
    }

    [Fact]
    public async Task Me_returns_the_registered_user()
    {
        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(_factory, email);

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/me");

        Assert.NotNull(session);
        Assert.Equal(email, session!.Email);
        Assert.NotEqual(Guid.Empty, session.Id);
    }

    [Fact]
    public async Task Me_without_a_cookie_returns_401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_seeded_dev_user_can_sign_in()
    {
        var client = await AuthenticatedClient.LoginAsync(
            _factory, SeedData.DevUserEmail, SeedData.DevUserPassword);

        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/me");

        Assert.Equal(SeedData.DevUserId, session!.Id);
    }

    [Fact]
    public async Task A_wrong_password_returns_401_with_a_generic_message()
    {
        var email = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(_factory, email);

        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new { Email = email, Password = "definitely wrong password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-credentials", body!["title"].ToString());
    }

    [Fact]
    public async Task An_unknown_email_returns_the_same_error_as_a_wrong_password()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { Email = "nobody-at-all@marketpulse.local", Password = "definitely wrong password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-credentials", body!["title"].ToString());
    }

    [Fact]
    public async Task Refreshing_rotates_the_refresh_cookie()
    {
        var client = await AuthenticatedClient.RegisterAsync(_factory);

        var response = await client.PostAsync("/api/v1/auth/refresh", content: null);

        response.EnsureSuccessStatusCode();
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("mp_refresh=", StringComparison.Ordinal));

        // The session still works after rotation.
        AuthenticatedClient.AttachCsrfHeader(client, response);
        var session = await client.GetFromJsonAsync<SessionResponse>("/api/v1/auth/me");
        Assert.NotNull(session);
    }

    [Fact]
    public async Task Logging_out_clears_the_session()
    {
        var client = await AuthenticatedClient.RegisterAsync(_factory);

        var logout = await client.PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var after = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Health_is_reachable_without_authentication()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 14: Repair the slice 1 integration tests**

Task 4 already made these files *compile*. What changes now is different: the watchlist endpoints require a session, so the tests must authenticate.

In `tests/MarketPulse.IntegrationTests/WatchlistApiTests.cs`, replace `InitializeAsync` and the field declarations so each run gets its own registered user instead of reusing the seeded dev account:

```csharp
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);
            b.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(DbContextOptions<MarketPulseDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });

        // A fresh account per test class run: an empty watchlist with no seeded items,
        // and no cross-test interference through the shared dev user.
        _client = await AuthenticatedClient.RegisterAsync(_factory);
    }
```

Delete the now-unused `using MarketPulse.Domain.Entities;` and the manual user/watchlist seeding block that Task 4 patched — registering a fresh user replaces it entirely. The four test methods themselves need no changes.

`WatchlistPersistenceTests.cs` needs nothing further: it exercises the repository directly rather than going through HTTP, so authorization does not apply to it.

`PriceStreamTests` connects to the hub, which is now `[Authorize]`. `HttpConnectionOptions.Cookies` is only honoured when the transport builds its own `HttpClientHandler`, and this test supplies `HttpMessageHandlerFactory` instead — so set the header directly.

Add this helper to `AuthenticatedClient`:

```csharp
    /// <summary>
    /// Registers a user and returns the raw `mp_access=…` cookie pair, for callers that
    /// need to set a Cookie header by hand rather than use an HttpClient cookie jar.
    /// </summary>
    public static async Task<string> RegisterAndGetAccessCookieAsync(
        WebApplicationFactory<Program> factory)
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/register",
            new { Email = NewEmail(), Password = ValidPassword });

        response.EnsureSuccessStatusCode();

        return response.Headers.GetValues("Set-Cookie")
            .Select(c => c.Split(';')[0])
            .Single(c => c.StartsWith("mp_access=", StringComparison.Ordinal));
    }
```

Then in `PriceStreamTests`, after the factory is built and before the connection:

```csharp
        var accessCookie = await AuthenticatedClient.RegisterAndGetAccessCookieAsync(factory);

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/prices", o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = accessCookie;
            })
            .Build();
```

Add a second test in the same file proving the hub is actually closed to anonymous clients:

```csharp
    [Fact]
    public async Task An_unauthenticated_client_cannot_connect_to_the_hub()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);
            b.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(DbContextOptions<MarketPulseDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/prices", o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        await connection.DisposeAsync();
    }
```

- [ ] **Step 15: Run the whole backend suite**

Run: `dotnet test`
Expected: every unit and integration test green, including the 12 new `AuthApiTests`. Fix failures before continuing — this is the task where the two halves meet, so a failure here is a real integration defect, not noise.

- [ ] **Step 16: Verify by hand against a real browser session**

```bash
docker compose up -d
dotnet run --project src/MarketPulse.Api &
sleep 10
curl -i -c /tmp/mp.jar -X POST http://localhost:5100/api/v1/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"dev@marketpulse.local","password":"DevPassw0rd!2026"}'
curl -i -b /tmp/mp.jar http://localhost:5100/api/v1/watchlist
curl -i http://localhost:5100/api/v1/watchlist
```

Expected: login returns 200 with three `Set-Cookie` headers; the cookie-bearing watchlist request returns 200 with the four seeded tickers; the bare request returns 401 with `application/problem+json`. Then `kill %1`.

- [ ] **Step 17: Commit**

```bash
git add -A
git commit -m "feat: replace the dev auth stub with real cookie-based authentication"
```

---

### Task 7: Api — CSRF and brute-force protection

**Files:**
- Create: `src/MarketPulse.Api/Middleware/CsrfMiddleware.cs`
- Modify: `src/MarketPulse.Api/Program.cs` (uncomment the middleware registration)
- Test: `tests/MarketPulse.IntegrationTests/CsrfAndRateLimitTests.cs`

**Interfaces:**
- Consumes: `AuthCookies` (Task 6), `CsrfValidationException` (Task 1), `AuthOptions` (Task 2).
- Produces: `CsrfMiddleware.HeaderName` = `"X-CSRF-Token"`.

- [ ] **Step 1: Write the failing CSRF and rate-limit tests**

Create `tests/MarketPulse.IntegrationTests/CsrfAndRateLimitTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using MarketPulse.Application.Watchlists;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class CsrfAndRateLimitTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Each test builds its own factory so it can dial the thresholds down. Sharing one
    /// rate-limiter partition across tests makes them order-dependent.
    /// </summary>
    private WebApplicationFactory<Program> CreateFactory(
        Dictionary<string, string?>? overrides = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);

            if (overrides is not null)
            {
                b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(overrides));
            }

            b.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(DbContextOptions<MarketPulseDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });

    [Fact]
    public async Task A_mutation_without_the_csrf_header_is_rejected_with_403()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);

        // Drop the header the helper attached — cookies alone must not be enough.
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");

        var response = await client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("csrf-failed", body!["title"].ToString());
    }

    [Fact]
    public async Task A_mutation_with_a_mismatched_csrf_header_is_rejected_with_403()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);

        client.DefaultRequestHeaders.Remove("X-CSRF-Token");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", "not-the-right-nonce");

        var response = await client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_mutation_with_the_matching_csrf_header_succeeds()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Reads_do_not_require_a_csrf_header()
    {
        using var factory = CreateFactory();
        var client = await AuthenticatedClient.RegisterAsync(factory);
        client.DefaultRequestHeaders.Remove("X-CSRF-Token");

        var response = await client.GetAsync("/api/v1/watchlist");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_does_not_require_a_csrf_header()
    {
        // The client cannot have a CSRF cookie before its first successful login.
        using var factory = CreateFactory();
        var email = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(factory, email);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_sixth_bad_password_locks_the_account_with_a_retry_after()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Auth:MaxFailedAttempts"] = "5",
            ["Auth:LockoutDuration"] = "00:15:00",
            ["Auth:LoginRequestsPerMinute"] = "1000"
        });

        var email = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(factory, email);
        var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { Email = email, Password = "wrong password entirely" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Even the correct password is now refused.
        var locked = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Email = email, Password = AuthenticatedClient.ValidPassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotNull(locked.Headers.RetryAfter);
    }

    [Fact]
    public async Task The_login_endpoint_is_rate_limited_per_ip()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Auth:LoginRequestsPerMinute"] = "3"
        });

        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { Email = "nobody@marketpulse.local", Password = "wrong password entirely" });
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~CsrfAndRateLimitTests`
Expected: the three CSRF-rejection tests fail (mutations currently succeed without a header) — `CsrfMiddleware` is not registered yet.

- [ ] **Step 3: Write `CsrfMiddleware`**

Create `src/MarketPulse.Api/Middleware/CsrfMiddleware.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using MarketPulse.Api.Authentication;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Api.Middleware;

/// <summary>
/// Double-submit cookie CSRF defence: an unsafe request must echo the (JS-readable)
/// mp_csrf cookie back in a header. A cross-site attacker can cause the browser to send
/// the cookie but cannot read it to set the header.
///
/// SameSite already blocks the common cases — this is defence in depth, not the primary
/// control. Chosen over the framework's IAntiforgery because that is oriented around MVC
/// form posts rather than a JSON API.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-CSRF-Token";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    /// <summary>
    /// Login and register are exempt: a client cannot hold a CSRF cookie before its first
    /// successful authentication. Both are rate limited instead.
    /// </summary>
    private static readonly string[] ExemptPaths =
        ["/api/v1/auth/login", "/api/v1/auth/register"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (SafeMethods.Contains(context.Request.Method)
            || ExemptPaths.Any(p => context.Request.Path.StartsWithSegments(p)))
        {
            await next(context);
            return;
        }

        var cookie = context.Request.Cookies[AuthCookies.Csrf];
        var header = context.Request.Headers[HeaderName].ToString();

        if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header) || !Matches(cookie, header))
        {
            // Thrown rather than written directly so ExceptionHandlingMiddleware, which sits
            // above this in the pipeline, produces the same ProblemDetails shape as
            // everything else.
            throw new CsrfValidationException();
        }

        await next(context);
    }

    private static bool Matches(string cookie, string header) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header));
}
```

- [ ] **Step 4: Register it**

In `src/MarketPulse.Api/Program.cs`, insert `app.UseMiddleware<CsrfMiddleware>();` between `app.UseRateLimiter();` and `app.UseAuthentication();`, so the pipeline reads:

```csharp
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
```

Order matters: `ExceptionHandlingMiddleware` must be above `CsrfMiddleware` to catch what it throws, and `UseCors` must be above it so a rejected preflight still gets its CORS headers.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~CsrfAndRateLimitTests`
Expected: 7 passed.

If `The_login_endpoint_is_rate_limited_per_ip` is flaky, it is because `RemoteIpAddress` is null under `WebApplicationFactory` and every test shares the `"unknown"` partition. The per-test factory plus a dedicated limit of 3 should isolate it; if it still bleeds, give that test its own partition key by sending a distinct `X-Forwarded-For` and keying the limiter off it — but only if you first confirm the bleed, rather than pre-emptively.

- [ ] **Step 6: Run the full backend suite**

Run: `dotnet test`
Expected: everything green.

- [ ] **Step 7: Commit**

```bash
git add src/MarketPulse.Api tests/MarketPulse.IntegrationTests/CsrfAndRateLimitTests.cs
git commit -m "feat: add CSRF double-submit checking and login rate limiting"
```

---

### Task 8: Cross-user isolation

Slice 1 scoped every query by `UserId` but only ever had one user in the database, so a query missing its `.Where` clause would pass every test in the suite. This task closes that.

**Files:**
- Test: `tests/MarketPulse.IntegrationTests/CrossUserIsolationTests.cs`

**Interfaces:**
- Consumes: `AuthenticatedClient` (Task 6). Produces nothing — this task adds tests only.

- [ ] **Step 1: Write the isolation tests**

Create `tests/MarketPulse.IntegrationTests/CrossUserIsolationTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using MarketPulse.Application.Watchlists;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class CrossUserIsolationTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);
            b.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(DbContextOptions<MarketPulseDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });

        _alice = await AuthenticatedClient.RegisterAsync(_factory);
        _bob = await AuthenticatedClient.RegisterAsync(_factory);
    }

    [Fact]
    public async Task Bob_cannot_see_what_Alice_added()
    {
        await _alice.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        var bobsList = await _bob.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");

        Assert.NotNull(bobsList);
        Assert.DoesNotContain(bobsList!.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public async Task Alice_and_Bob_have_different_watchlists()
    {
        var alices = await _alice.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");
        var bobs = await _bob.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");

        Assert.NotEqual(alices!.Id, bobs!.Id);
    }

    [Fact]
    public async Task Bob_adding_the_same_ticker_is_not_a_duplicate()
    {
        // The uniqueness rule is per watchlist, not global. If this 409s, the query is
        // reaching across users.
        await _alice.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("NDQ"));

        var bobsAdd = await _bob.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("NDQ"));

        bobsAdd.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bob_deleting_a_ticker_does_not_remove_it_from_Alices_list()
    {
        await _alice.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("VAS"));

        var bobsDelete = await _bob.DeleteAsync("/api/v1/watchlist/items/VAS");

        // Bob has no such item: a 4xx is correct, silent success is not.
        Assert.False(bobsDelete.IsSuccessStatusCode);

        var alicesList = await _alice.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");
        Assert.Contains(alicesList!.Items, i => i.Ticker == "VAS");
    }

    [Fact]
    public async Task A_logged_out_session_cannot_read_the_watchlist()
    {
        await _alice.PostAsync("/api/v1/auth/logout", content: null);

        var response = await _alice.GetAsync("/api/v1/watchlist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public Task DisposeAsync()
    {
        _alice.Dispose();
        _bob.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~CrossUserIsolationTests`
Expected: 5 passed. They should pass on the first run — slice 1's scoping is believed correct, and these tests exist to prove it rather than to drive new code.

**If any fails, stop and treat it as a real defect**, not a test bug: it means a watchlist query is missing its user scope. Fix the query, and note in the task report that slice 1 shipped an isolation bug that only two users could reveal.

- [ ] **Step 3: Commit**

```bash
git add tests/MarketPulse.IntegrationTests/CrossUserIsolationTests.cs
git commit -m "test: prove watchlist data is isolated between users"
```

---

### Task 9: api-client — cookies, CSRF, and single-flight refresh

**Files:**
- Modify: `packages/api-client/src/client.ts`
- Modify: `packages/api-client/src/schemas.ts`
- Test: `packages/api-client/src/client.test.ts`

**Interfaces:**
- Consumes: the endpoints from Task 6.
- Produces, on the object returned by `createApiClient(baseUrl)`:
  - existing `getWatchlist`, `addItem`, `removeItem` — unchanged signatures
  - `register(email: string, password: string): Promise<Session>`
  - `login(email: string, password: string): Promise<Session>`
  - `logout(): Promise<void>`
  - `me(signal?: AbortSignal): Promise<Session>`
  - exported `sessionSchema`, `type Session = { id: string; email: string }`

- [ ] **Step 1: Add the session schema**

In `packages/api-client/src/schemas.ts`, add before the type exports:

```typescript
export const sessionSchema = z.object({
  id: z.string(),
  email: z.string(),
});
```

and add to the type exports at the bottom:

```typescript
export type Session = z.infer<typeof sessionSchema>;
```

- [ ] **Step 2: Write the failing client tests**

Create `packages/api-client/src/client.test.ts`:

```typescript
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, createApiClient } from './client';

const BASE = 'http://api.test';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('createApiClient', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    // The CSRF cookie is readable by JavaScript by design — that is the "double submit".
    vi.stubGlobal('document', { cookie: 'mp_csrf=nonce-123' });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends credentials so the httpOnly cookies travel', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ id: 'w1', items: [] }));

    await createApiClient(BASE).getWatchlist();

    expect(fetchMock.mock.calls[0]?.[1]).toMatchObject({ credentials: 'include' });
  });

  it('attaches the CSRF header to mutations', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ id: 'w1', items: [] }));

    await createApiClient(BASE).addItem('IVV');

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    expect((init.headers as Record<string, string>)['X-CSRF-Token']).toBe('nonce-123');
  });

  it('does not attach the CSRF header to reads', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ id: 'w1', items: [] }));

    await createApiClient(BASE).getWatchlist();

    const init = fetchMock.mock.calls[0]?.[1] as RequestInit;
    expect((init.headers as Record<string, string>)['X-CSRF-Token']).toBeUndefined();
  });

  it('refreshes once and retries when a request returns 401', async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: 'unauthenticated', status: 401 }, 401))
      .mockResolvedValueOnce(jsonResponse({ id: 'u1', email: 'a@b.com' })) // refresh
      .mockResolvedValueOnce(jsonResponse({ id: 'w1', items: [] })); // retry

    const watchlist = await createApiClient(BASE).getWatchlist();

    expect(watchlist.items).toEqual([]);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[1]?.[0]).toBe(`${BASE}/api/v1/auth/refresh`);
  });

  it('gives up after one failed refresh rather than looping', async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ title: 'unauthenticated', status: 401 }, 401))
      .mockResolvedValueOnce(jsonResponse({ title: 'session-revoked', status: 401 }, 401));

    const client = createApiClient(BASE);

    await expect(client.getWatchlist()).rejects.toBeInstanceOf(ApiError);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('refreshes only once for concurrent 401s', async () => {
    // Three parallel requests hitting an expired token must produce ONE refresh call,
    // not three. This is what the single-flight promise is for.
    fetchMock.mockImplementation((url: string) => {
      if (url.endsWith('/auth/refresh')) {
        return Promise.resolve(jsonResponse({ id: 'u1', email: 'a@b.com' }));
      }
      if (fetchMock.mock.calls.filter((c) => !String(c[0]).endsWith('/auth/refresh')).length <= 3) {
        return Promise.resolve(jsonResponse({ title: 'unauthenticated', status: 401 }, 401));
      }
      return Promise.resolve(jsonResponse({ id: 'w1', items: [] }));
    });

    const client = createApiClient(BASE);
    await Promise.all([client.getWatchlist(), client.getWatchlist(), client.getWatchlist()]);

    const refreshCalls = fetchMock.mock.calls.filter((c) =>
      String(c[0]).endsWith('/auth/refresh'),
    );
    expect(refreshCalls).toHaveLength(1);
  });

  it('does not try to refresh a failed login', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ title: 'invalid-credentials', status: 401 }, 401),
    );

    await expect(
      createApiClient(BASE).login('a@b.com', 'wrong password here'),
    ).rejects.toBeInstanceOf(ApiError);

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('surfaces the problem details title as the error code', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        { title: 'duplicate-ticker', status: 409, detail: 'nope', correlationId: 'abc' },
        409,
      ),
    );

    await expect(createApiClient(BASE).addItem('IVV')).rejects.toMatchObject({
      status: 409,
      errorCode: 'duplicate-ticker',
      correlationId: 'abc',
    });
  });
});
```

- [ ] **Step 3: Run them and confirm they fail**

Run: `pnpm --filter @marketpulse/api-client test`
Expected: failures — no `credentials`, no CSRF header, no refresh retry, and `login`/`me` do not exist.

- [ ] **Step 4: Rewrite `client.ts`**

Replace `packages/api-client/src/client.ts` entirely. The important structural change is that `request` now lives **inside** `createApiClient`, so the single-flight refresh promise is per-client state in a closure rather than module-global — which keeps tests independent of each other.

```typescript
import {
  problemDetailsSchema,
  sessionSchema,
  watchlistSchema,
  type Session,
  type Watchlist,
} from './schemas';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly errorCode: string,
    message: string,
    readonly correlationId?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);

/** The CSRF cookie is deliberately not httpOnly — reading it here is the "double" in double-submit. */
function readCsrfToken(): string | undefined {
  if (typeof document === 'undefined') return undefined;

  const match = document.cookie
    .split(';')
    .map((c) => c.trim())
    .find((c) => c.startsWith('mp_csrf='));

  return match ? decodeURIComponent(match.slice('mp_csrf='.length)) : undefined;
}

export function createApiClient(baseUrl: string) {
  const root = baseUrl.replace(/\/$/, '');

  /**
   * One in-flight refresh at a time. Ten components hitting an expired access token
   * simultaneously must produce one refresh call, not ten — and the nine that did not
   * initiate it still need to await the same result before retrying.
   */
  let refreshInFlight: Promise<boolean> | null = null;

  function refreshOnce(): Promise<boolean> {
    refreshInFlight ??= fetch(`${root}/api/v1/auth/refresh`, {
      method: 'POST',
      credentials: 'include',
      headers: csrfHeaders('POST'),
    })
      .then((response) => response.ok)
      .catch(() => false)
      .finally(() => {
        refreshInFlight = null;
      });

    return refreshInFlight;
  }

  function csrfHeaders(method: string): Record<string, string> {
    if (SAFE_METHODS.has(method.toUpperCase())) return {};

    const token = readCsrfToken();
    return token ? { 'X-CSRF-Token': token } : {};
  }

  async function request<T>(
    path: string,
    init: RequestInit,
    parse: (data: unknown) => T,
    allowRefresh = true,
  ): Promise<T> {
    const method = init.method ?? 'GET';

    const response = await fetch(`${root}${path}`, {
      ...init,
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        ...csrfHeaders(method),
        ...init.headers,
      },
    });

    // An expired access token is the expected steady state every 15 minutes, so it is
    // handled here rather than surfaced to every caller.
    if (response.status === 401 && allowRefresh) {
      const refreshed = await refreshOnce();
      if (refreshed) {
        return request(path, init, parse, false);
      }
    }

    const body: unknown = await response.json().catch(() => null);

    if (!response.ok) {
      const problem = problemDetailsSchema.safeParse(body);
      throw new ApiError(
        response.status,
        problem.success ? problem.data.title : 'unknown-error',
        problem.success ? (problem.data.detail ?? problem.data.title) : response.statusText,
        problem.success ? problem.data.correlationId : undefined,
      );
    }

    return parse(body);
  }

  return {
    register: (email: string, password: string): Promise<Session> =>
      request(
        '/api/v1/auth/register',
        { method: 'POST', body: JSON.stringify({ email, password }) },
        (d) => sessionSchema.parse(d),
        false,
      ),

    // allowRefresh is false: a 401 from login means the credentials are wrong, and
    // trying to refresh in response would be nonsense.
    login: (email: string, password: string): Promise<Session> =>
      request(
        '/api/v1/auth/login',
        { method: 'POST', body: JSON.stringify({ email, password }) },
        (d) => sessionSchema.parse(d),
        false,
      ),

    logout: (): Promise<void> =>
      request('/api/v1/auth/logout', { method: 'POST' }, () => undefined, false),

    me: (signal?: AbortSignal): Promise<Session> =>
      request('/api/v1/auth/me', { method: 'GET', signal }, (d) => sessionSchema.parse(d)),

    getWatchlist: (signal?: AbortSignal): Promise<Watchlist> =>
      request('/api/v1/watchlist', { method: 'GET', signal }, (d) => watchlistSchema.parse(d)),

    addItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        '/api/v1/watchlist/items',
        { method: 'POST', body: JSON.stringify({ ticker }), signal },
        (d) => watchlistSchema.parse(d),
      ),

    removeItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        `/api/v1/watchlist/items/${encodeURIComponent(ticker)}`,
        { method: 'DELETE', signal },
        (d) => watchlistSchema.parse(d),
      ),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
```

Note the signature change: `request` now takes a **path** rather than a full URL, since it builds the URL from `root` itself.

- [ ] **Step 5: Run the client tests and confirm they pass**

Run: `pnpm --filter @marketpulse/api-client test`
Expected: all passing, including the pre-existing `schemas.test.ts`.

- [ ] **Step 6: Typecheck**

Run: `pnpm typecheck`
Expected: clean. The dashboard consumes `apiClient` and must still compile against the unchanged watchlist method signatures.

- [ ] **Step 7: Commit**

```bash
git add packages/api-client
git commit -m "feat: send cookies, attach CSRF tokens, and refresh expired sessions once"
```

---

### Task 10: Dashboard — routing, login, register, protected routes

**Files:**
- Create: `apps/dashboard/src/features/auth/useSession.ts`
- Create: `apps/dashboard/src/features/auth/LoginScreen.tsx`
- Create: `apps/dashboard/src/features/auth/RegisterScreen.tsx`
- Create: `apps/dashboard/src/features/auth/ProtectedRoute.tsx`
- Modify: `apps/dashboard/src/App.tsx`
- Modify: `apps/dashboard/package.json`
- Test: `apps/dashboard/src/features/auth/LoginScreen.test.tsx`
- Test: `apps/dashboard/src/features/auth/ProtectedRoute.test.tsx`

**Interfaces:**
- Consumes: `apiClient` from `../../api` (Task 9).
- Produces:
  - `useSession()` — TanStack Query over `apiClient.me()`, key `['session']`, `retry: false`
  - `useLogin()`, `useRegister()`, `useLogout()` mutations
  - `<ProtectedRoute>{children}</ProtectedRoute>`
  - Routes: `/login`, `/register`, `/` (protected)

- [ ] **Step 1: Add the router**

```bash
pnpm --filter @marketpulse/dashboard add react-router-dom
```

Confirm it landed in `apps/dashboard/package.json` dependencies and that `pnpm-lock.yaml` updated.

- [ ] **Step 2: Write `useSession`**

Create `apps/dashboard/src/features/auth/useSession.ts`:

```typescript
import { ApiError, type Session } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

export const sessionKey = ['session'] as const;

/**
 * There is no token in the client to store — the session lives entirely in httpOnly
 * cookies the browser manages. "Am I signed in?" is therefore a server question, and
 * this query is the answer.
 */
export function useSession() {
  return useQuery({
    queryKey: sessionKey,
    queryFn: ({ signal }) => apiClient.me(signal),
    // A 401 is the expected answer for a signed-out visitor, not a transient failure.
    retry: false,
  });
}

export function useLogin() {
  const queryClient = useQueryClient();

  return useMutation<Session, ApiError, { email: string; password: string }>({
    mutationFn: ({ email, password }) => apiClient.login(email, password),
    onSuccess: (session) => queryClient.setQueryData(sessionKey, session),
  });
}

export function useRegister() {
  const queryClient = useQueryClient();

  return useMutation<Session, ApiError, { email: string; password: string }>({
    mutationFn: ({ email, password }) => apiClient.register(email, password),
    onSuccess: (session) => queryClient.setQueryData(sessionKey, session),
  });
}

export function useLogout() {
  const queryClient = useQueryClient();

  return useMutation<void, ApiError, void>({
    mutationFn: () => apiClient.logout(),
    // Drop every cached query, not just the session: the watchlist in the cache belongs
    // to the user who just signed out.
    onSuccess: () => queryClient.clear(),
  });
}
```

- [ ] **Step 3: Write the failing `ProtectedRoute` test**

Create `apps/dashboard/src/features/auth/ProtectedRoute.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { ProtectedRoute } from './ProtectedRoute';

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderAt(path: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/login" element={<p>Sign in page</p>} />
          <Route
            path="/"
            element={
              <ProtectedRoute>
                <p>Secret watchlist</p>
              </ProtectedRoute>
            }
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ProtectedRoute', () => {
  it('renders the children when the session is valid', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/auth/me', () =>
        HttpResponse.json({ id: 'u1', email: 'someone@marketpulse.local' }),
      ),
    );

    renderAt('/');

    expect(await screen.findByText('Secret watchlist')).toBeInTheDocument();
  });

  it('redirects to the login page when the session is missing', async () => {
    server.use(
      http.get('http://localhost:5100/api/v1/auth/me', () =>
        HttpResponse.json({ title: 'unauthenticated', status: 401 }, { status: 401 }),
      ),
      http.post('http://localhost:5100/api/v1/auth/refresh', () =>
        HttpResponse.json({ title: 'session-revoked', status: 401 }, { status: 401 }),
      ),
    );

    renderAt('/');

    expect(await screen.findByText('Sign in page')).toBeInTheDocument();
  });
});
```

Note the second handler: a 401 on `/me` makes the client attempt exactly one refresh, so MSW must answer that call or `onUnhandledRequest: 'error'` fails the test.

- [ ] **Step 4: Run it and confirm it fails**

Run: `pnpm --filter @marketpulse/dashboard test`
Expected: failure — `ProtectedRoute` does not exist.

- [ ] **Step 5: Write `ProtectedRoute`**

Create `apps/dashboard/src/features/auth/ProtectedRoute.tsx`:

```tsx
import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { useSession } from './useSession';

export function ProtectedRoute({ children }: { children: ReactNode }) {
  const { isPending, isError } = useSession();

  if (isPending) return <p>Checking your session…</p>;
  if (isError) return <Navigate to="/login" replace />;

  return <>{children}</>;
}
```

- [ ] **Step 6: Write the failing login screen test**

Create `apps/dashboard/src/features/auth/LoginScreen.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { MemoryRouter } from 'react-router-dom';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { LoginScreen } from './LoginScreen';

const server = setupServer();

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <LoginScreen />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('LoginScreen', () => {
  it('shows the server message when the credentials are wrong', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/auth/login', () =>
        HttpResponse.json(
          {
            title: 'invalid-credentials',
            status: 401,
            detail: 'Email or password is incorrect.',
          },
          { status: 401 },
        ),
      ),
    );

    renderScreen();

    await userEvent.type(screen.getByLabelText(/email/i), 'someone@marketpulse.local');
    await userEvent.type(screen.getByLabelText(/password/i), 'wrong password here');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('Email or password is incorrect.'),
    );
  });

  it('tells the user how long to wait when the account is locked', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/auth/login', () =>
        HttpResponse.json(
          {
            title: 'account-locked',
            status: 429,
            detail: 'Too many failed attempts. Try again later.',
          },
          { status: 429 },
        ),
      ),
    );

    renderScreen();

    await userEvent.type(screen.getByLabelText(/email/i), 'someone@marketpulse.local');
    await userEvent.type(screen.getByLabelText(/password/i), 'wrong password here');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/too many failed attempts/i),
    );
  });
});
```

- [ ] **Step 7: Write `LoginScreen` and `RegisterScreen`**

Create `apps/dashboard/src/features/auth/LoginScreen.tsx`:

```tsx
import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useLogin } from './useSession';

export function LoginScreen() {
  const login = useLogin();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    login.mutate({ email, password }, { onSuccess: () => navigate('/', { replace: true }) });
  }

  return (
    <section aria-labelledby="login-heading">
      <h2 id="login-heading">Sign in</h2>

      <form onSubmit={handleSubmit}>
        <label htmlFor="login-email">Email</label>
        <input
          id="login-email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />

        <label htmlFor="login-password">Password</label>
        <input
          id="login-password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />

        <button type="submit" disabled={login.isPending}>
          Sign in
        </button>
      </form>

      {login.isError && (
        <p role="alert">
          {login.error.message}
          {login.error.correlationId && ` (ref: ${login.error.correlationId})`}
        </p>
      )}

      <p>
        No account? <Link to="/register">Create one</Link>
      </p>
    </section>
  );
}
```

Create `apps/dashboard/src/features/auth/RegisterScreen.tsx` — the same shape, with `useRegister`, `autoComplete="new-password"`, a heading of "Create an account", a submit button labelled "Create account", and a link back to `/login`. Include the client-side length hint so the user is not told about the 12-character minimum only by a round trip:

```tsx
import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useRegister } from './useSession';

const MINIMUM_PASSWORD_LENGTH = 12;

export function RegisterScreen() {
  const register = useRegister();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  const tooShort = password.length > 0 && password.length < MINIMUM_PASSWORD_LENGTH;

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    if (tooShort) return;
    register.mutate({ email, password }, { onSuccess: () => navigate('/', { replace: true }) });
  }

  return (
    <section aria-labelledby="register-heading">
      <h2 id="register-heading">Create an account</h2>

      <form onSubmit={handleSubmit}>
        <label htmlFor="register-email">Email</label>
        <input
          id="register-email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />

        <label htmlFor="register-password">Password</label>
        <input
          id="register-password"
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          aria-describedby="password-hint"
          required
        />
        <p id="password-hint">
          At least {MINIMUM_PASSWORD_LENGTH} characters. A memorable phrase beats a short,
          complicated password.
        </p>

        <button type="submit" disabled={register.isPending || tooShort}>
          Create account
        </button>
      </form>

      {register.isError && <p role="alert">{register.error.message}</p>}

      <p>
        Already registered? <Link to="/login">Sign in</Link>
      </p>
    </section>
  );
}
```

- [ ] **Step 8: Wire the router**

Replace `apps/dashboard/src/App.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import { LoginScreen } from './features/auth/LoginScreen';
import { ProtectedRoute } from './features/auth/ProtectedRoute';
import { RegisterScreen } from './features/auth/RegisterScreen';
import { SignOutButton } from './features/auth/SignOutButton';
import { WatchlistScreen } from './features/watchlist/WatchlistScreen';

const queryClient = new QueryClient();

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <header>
          <h1>MarketPulse Pro</h1>
          <SignOutButton />
        </header>
        <main>
          <Routes>
            <Route path="/login" element={<LoginScreen />} />
            <Route path="/register" element={<RegisterScreen />} />
            <Route
              path="/"
              element={
                <ProtectedRoute>
                  <WatchlistScreen />
                </ProtectedRoute>
              }
            />
          </Routes>
        </main>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
```

Create `apps/dashboard/src/features/auth/SignOutButton.tsx`:

```tsx
import { useNavigate } from 'react-router-dom';
import { useLogout, useSession } from './useSession';

export function SignOutButton() {
  const { data: session } = useSession();
  const logout = useLogout();
  const navigate = useNavigate();

  if (!session) return null;

  return (
    <div>
      <span>{session.email}</span>
      <button
        type="button"
        onClick={() => logout.mutate(undefined, { onSuccess: () => navigate('/login') })}
      >
        Sign out
      </button>
    </div>
  );
}
```

- [ ] **Step 9: Fix the existing watchlist test**

`WatchlistScreen.test.tsx` renders the screen directly and does not touch the router, so it should still pass. Run it and confirm:

Run: `pnpm --filter @marketpulse/dashboard test`
Expected: all passing — the two new auth suites plus the existing watchlist, price cell, stream reducer and clock tests.

If `WatchlistScreen.test.tsx` now fails with an unhandled `/api/v1/auth/refresh` request, add the refresh handler to its `setupServer` call the same way `ProtectedRoute.test.tsx` does.

- [ ] **Step 10: Typecheck and build**

Run: `pnpm typecheck && pnpm --filter @marketpulse/dashboard build`
Expected: both clean.

- [ ] **Step 11: Commit**

```bash
git add apps/dashboard package.json pnpm-lock.yaml
git commit -m "feat: add login, registration, and protected routes to the dashboard"
```

---

### Task 11: Playwright E2E and the CI job

This pays the debt slice 1 recorded in `TESTING.md`: *"Playwright arrives with real auth in slice 2 — a journey through a fake login tests the fake."*

**Files:**
- Create: `tests/e2e/package.json`
- Create: `tests/e2e/playwright.config.ts`
- Create: `tests/e2e/tsconfig.json`
- Create: `tests/e2e/specs/authentication.spec.ts`
- Modify: `pnpm-workspace.yaml`
- Modify: `package.json` (root)
- Modify: `.github/workflows/ci.yml`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: the running API (Task 6) and dashboard (Task 10).
- Produces: `pnpm e2e` at the repo root.

- [ ] **Step 1: Add the workspace package**

Add `tests/*` to `pnpm-workspace.yaml`:

```yaml
packages:
  - "apps/*"
  - "packages/*"
  - "tests/e2e"
allowBuilds:
  esbuild: true
  msw: true
```

Create `tests/e2e/package.json`:

```json
{
  "name": "@marketpulse/e2e",
  "private": true,
  "type": "module",
  "scripts": {
    "e2e": "playwright test",
    "typecheck": "tsc --noEmit",
    "lint": "tsc --noEmit",
    "test": "echo \"e2e runs via pnpm e2e\" && exit 0"
  },
  "devDependencies": {
    "@playwright/test": "^1.48.0",
    "typescript": "^5.6.0"
  }
}
```

The `test` script is a deliberate no-op so `pnpm -r test` (which CI runs) does not try to start browsers on every commit. E2E gets its own CI job.

Create `tests/e2e/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "skipLibCheck": true,
    "noEmit": true,
    "types": ["node"]
  },
  "include": ["specs", "playwright.config.ts"]
}
```

Add the root script to `package.json`:

```json
    "e2e": "pnpm --filter @marketpulse/e2e e2e"
```

Then install and fetch the browser:

```bash
pnpm install
pnpm --filter @marketpulse/e2e exec playwright install --with-deps chromium
```

- [ ] **Step 2: Write the Playwright config**

Create `tests/e2e/playwright.config.ts`:

```typescript
import { defineConfig } from '@playwright/test';

const API_URL = 'http://localhost:5100';
const APP_URL = 'http://localhost:4173';

export default defineConfig({
  testDir: './specs',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? 'list' : 'html',

  use: {
    baseURL: APP_URL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },

  // Both servers must be up before any spec runs. /health exists precisely so Playwright
  // has an unauthenticated 200 to poll — /api/v1/auth/me answers 401 by design.
  webServer: [
    {
      command: 'dotnet run --project ../../src/MarketPulse.Api --launch-profile http',
      url: `${API_URL}/health`,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
      stdout: 'pipe',
      stderr: 'pipe',
    },
    {
      command: 'pnpm --filter @marketpulse/dashboard preview --port 4173 --strictPort',
      url: APP_URL,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
});
```

The dashboard's `preview` command serves the production build, so CI must run `pnpm --filter @marketpulse/dashboard build` first. That script does not exist yet — Step 4 adds it.

- [ ] **Step 3: Write the journey**

Create `tests/e2e/specs/authentication.spec.ts`:

```typescript
import { expect, test } from '@playwright/test';

const PASSWORD = 'correct horse battery staple';

function uniqueEmail(): string {
  return `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}@marketpulse.local`;
}

test('a new user can register, add a ticker, watch it tick, and sign out', async ({ page }) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();

  // Registration lands on the protected watchlist.
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  // A brand new account starts empty.
  await expect(page.getByRole('listitem')).toHaveCount(0);

  await page.getByLabel('Add ticker').fill('IVV');
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByText('IVV')).toBeVisible();

  // The price cell is fed by SignalR, which authenticated off the same cookie. If the
  // hub rejected the connection this stays at the em-dash placeholder forever.
  // PriceCell already exposes aria-label={`${ticker} price`} — no source change needed.
  await expect(page.getByLabel('IVV price')).toHaveText(/^\$\d/, { timeout: 15_000 });

  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/login$/);

  // The protected route is genuinely closed now, not merely hidden.
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('the session survives a full page reload', async ({ page }) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();

  await page.reload();

  // No re-login: the cookie outlived the page, which is the entire point of not
  // holding the token in JavaScript memory.
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();
});

test('signing in with the wrong password shows an error and stays on the login page', async ({
  page,
}) => {
  const email = uniqueEmail();

  await page.goto('/register');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'Watchlist' })).toBeVisible();
  await page.getByRole('button', { name: 'Sign out' }).click();

  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill('definitely the wrong password');
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByRole('alert')).toContainText(/incorrect/i);
  await expect(page).toHaveURL(/\/login$/);
});
```

- [ ] **Step 4: Add the `preview` script**

`apps/dashboard/package.json` has no `preview` script — the Playwright config depends on it. Add it to the scripts block:

```json
    "preview": "vite preview",
```

No change is needed to `PriceCell.tsx`: it already renders `aria-label={`${ticker} price`}`, which is the accessible hook the spec selects on. Preferring the existing accessible name over a new `data-testid` also means the E2E asserts against what a screen reader would announce.

- [ ] **Step 5: Ignore Playwright's output**

Append to `.gitignore`:

```
# Playwright
tests/e2e/test-results/
tests/e2e/playwright-report/
tests/e2e/blob-report/
```

- [ ] **Step 6: Run the suite locally**

```bash
docker compose up -d
pnpm --filter @marketpulse/dashboard build
pnpm e2e
```

Expected: 3 passed. If the API server fails to start, run it by hand and read its output — the most likely cause is `ValidateOnStart` rejecting a missing `Jwt:SigningKey`, which means Task 6 Step 11's `appsettings.Development.json` edit did not land.

- [ ] **Step 7: Add the CI job**

Append to `.github/workflows/ci.yml`:

```yaml
  e2e:
    runs-on: ubuntu-latest
    needs: [backend, frontend]
    services:
      sqlserver:
        image: mcr.microsoft.com/mssql/server:2022-latest
        env:
          ACCEPT_EULA: "Y"
          MSSQL_SA_PASSWORD: "Local!Dev!Pass123"
          MSSQL_PID: Developer
        ports:
          - 1433:1433
        options: >-
          --health-cmd "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Local!Dev!Pass123' -C -Q 'SELECT 1'"
          --health-interval 10s
          --health-timeout 5s
          --health-retries 12
          --health-start-period 30s
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - uses: pnpm/action-setup@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '24'
          cache: pnpm
      - run: pnpm install --frozen-lockfile
      - run: pnpm --filter @marketpulse/e2e exec playwright install --with-deps chromium
      - run: pnpm --filter @marketpulse/dashboard build
      - run: pnpm e2e
        env:
          ASPNETCORE_ENVIRONMENT: Development
      - uses: actions/upload-artifact@v4
        if: failure()
        with:
          name: playwright-report
          path: tests/e2e/playwright-report/
          retention-days: 7
```

The job runs `Development` deliberately: that is what makes the cookies non-`Secure` so they work over plain HTTP on the runner, and it is what supplies the dev signing key.

- [ ] **Step 8: Commit**

```bash
git add tests/e2e pnpm-workspace.yaml package.json pnpm-lock.yaml .github/workflows/ci.yml .gitignore apps/dashboard/src/features/prices/PriceCell.tsx
git commit -m "test: add a Playwright journey through registration, ticking prices, and sign-out"
```

---

### Task 12: Documentation

**Files:**
- Create: `docs/adr/003-cookie-based-sessions.md`
- Modify: `docs/TESTING.md`
- Modify: `README.md`

**Interfaces:** none — documentation only.

- [ ] **Step 1: Write ADR-003**

Create `docs/adr/003-cookie-based-sessions.md`, following the structure of ADR-001 and ADR-002 (read one first — each has a rejected-alternatives section, which is the part that carries the weight). It must cover:

- **Context:** slice 1 shipped a dev identity stub; slice 2 needs real sessions for a browser SPA.
- **Decision:** JWT access + rotating refresh tokens, both in httpOnly cookies, double-submit CSRF.
- **Rejected — bearer token in JS memory:** the conventional SPA pattern, but the IETF *OAuth 2.0 for Browser-Based Applications* BCP discourages holding tokens in the browser at all, because any XSS retrieves them. Cookie storage keeps the token out of JavaScript's reach entirely.
- **Rejected — full ASP.NET Core Identity:** `IdentityUser<Guid>` is an Infrastructure type; adopting it means either a second parallel user model kept in sync with `Domain.User`, or a `Domain` → Identity reference that breaks the dependency-rule test. We took the audited `PasswordHasher<T>` and nothing else.
- **Rejected — an external IdP (Entra ID / Auth0 / Keycloak):** what most enterprises actually do, and the right call for a real product. Rejected here because delegating means writing almost no auth code, and the token lifecycle is the thing this project exists to demonstrate.
- **Rejected — opaque server-side sessions:** simpler, and arguably better for a single API, but banks none of the JWT signing/claims/validation material.
- **Consequences:** cookie auth requires same-site deployment — `app.example.com` + `api.example.com` would silently stop sending the cookies, so the deployment slice must serve both from one site or add the BFF proxy hop. Note this is the deliberate step toward BFF, not an accident.
- **Accepted limitations:** account enumeration on registration; the seeded dev account with a committed password hash.

- [ ] **Step 2: Update `TESTING.md`**

Read `docs/TESTING.md` and replace the "No E2E suite" gap — it is now paid. State what the Playwright suite covers (three journeys: registration through to a live tick and sign-out; session survival across a reload; the wrong-password path), how to run it (`pnpm e2e`, after `docker compose up -d` and a dashboard build), and that it runs in CI as its own job.

Add the two new deliberate gaps from the spec:

- **No load or brute-force simulation.** The rate limiter is tested for behaviour at its threshold, not under real concurrent load.
- **No test that `Secure` cookies work over HTTPS.** The whole suite runs in Development where `Secure` is off by design; the attribute itself is covered by `AuthCookiesTests` as a pure function instead.

Leave the "no SignalR transport test" and "no coverage threshold" entries as they are.

- [ ] **Step 3: Update the README**

In `README.md`, update the getting-started section so a clean clone can sign in:

- Document the seeded credentials: `dev@marketpulse.local` / `DevPassw0rd!2026`, with a one-line note that this account is a development convenience the hardening slice removes.
- Note that registering a new account is the other path, and that new accounts start with an empty watchlist while the dev account has four seeded tickers.
- Add `pnpm e2e` to the commands list, with its prerequisites.
- Update any statement that the app has no authentication.

Run `grep -rn -i "devauth\|dev user\|no auth\|authentication" README.md docs/*.md` and fix every stale claim the search turns up.

- [ ] **Step 4: Verify the getting-started block actually works**

Follow the README from a clean state, exactly as written:

```bash
git stash list && docker compose down -v && docker compose up -d
dotnet run --project src/MarketPulse.Api &
pnpm --filter @marketpulse/dashboard dev
```

Sign in through the browser at `http://localhost:5173/login` with the documented credentials. Confirm: four seeded tickers appear, prices tick once per second, a page reload does not force a re-login, and signing out redirects to `/login`. Slice 1's final review found two Criticals in exactly this block — do not skip it.

- [ ] **Step 5: Commit**

```bash
git add docs README.md
git commit -m "docs: add ADR-003 and document authentication in the README and testing strategy"
```

---

## Final verification

Before opening the pull request:

- [ ] `dotnet test` — all unit and integration tests green
- [ ] `pnpm test` — api-client and dashboard green
- [ ] `pnpm typecheck` — clean
- [ ] `pnpm --filter @marketpulse/dashboard build` — clean
- [ ] `pnpm e2e` — 3 passed
- [ ] `docker build -f src/MarketPulse.Api/Dockerfile .` — succeeds
- [ ] `grep -rn "DevAuthMiddleware" src/ tests/ docs/ README.md` — returns nothing outside the slice 1 spec and plan, which are historical records and stay as written
- [ ] Every done-criterion in the spec is checked off
- [ ] Merge `feature/slice-2-authentication` into `test` and confirm CI is green, including the new `e2e` job

---

## Plan self-review

Checked after writing, against the spec.

**Spec coverage.** Every spec section maps to a task: token lifecycle → 3 and 5; API surface → 6; CSRF and rate limiting → 7; error taxonomy → 6 (status codes) and 7 (403); password policy → 2; the seeded dev account → 4; the same-site deployment constraint → 12 (ADR consequences); every testing row → 1, 2, 3, 5, 6, 7, 8, 9, 10, 11; every done-criterion → the final verification list.

**Two spec items deliberately spread rather than given their own task.** Account enumeration is documented in Task 12's ADR and enforced by the two `AuthApiTests` that assert unknown-email and wrong-password return identical responses. The `UnauthorizedAccessException` → 401 fix from the slice 1 ledger is folded into Task 6 Step 7, where the middleware is already open.

**Known sequencing note.** `CsrfMiddleware` is created and registered together in Task 7; Task 6's pipeline deliberately omits it rather than leaving a commented-out placeholder. Tasks 6 and 7 must therefore run in order, which the plan already requires.

**Type consistency.** `AuthResult` is constructed in `SessionFactory.IssueAsync` and in `RefreshSessionHandler` — both pass the same six positional arguments in the same order. `AuthCookies.Build` has one signature, used in three places. The api-client's `request` takes a path, not a URL, in every call site.

**Two values are generated rather than written down**, because both must be real PBKDF2 output that no plan text can supply: the seeded dev user's hash (Task 4 Step 1) and the login timing-equalisation hash (Task 5 Step 5). Each has an explicit generate-then-paste procedure. A fabricated constant would fail hash-format parsing — silently, in the timing case, defeating the very thing it exists to do.

**Four defects found and fixed during this review.** The timing-equalisation constant was originally an invented base64 string that would have parsed as malformed and returned early. `AuthController` took an `IOptions<JwtOptions>` it never read, which CS9113 turns into a build error under this repo's `TreatWarningsAsErrors`. The E2E spec selected a `data-testid` that would have required editing `PriceCell`, when the component already exposes `aria-label={`${ticker} price`}`. And `apps/dashboard/package.json` has no `preview` script, which the Playwright `webServer` config depends on — now added explicitly rather than assumed.

