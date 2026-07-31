# Slice 1 — Walking Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a watchlist of ASX ETFs that persists to SQL Server and updates live over SignalR, wired end to end through every architectural layer, with green CI.

**Architecture:** Clean Architecture with the dependency rule enforced by project references (`Domain` → nothing, `Application` → `Domain`, `Infrastructure` → `Application`, `Api` → `Infrastructure`). CQRS handlers via MediatR. Real-time delivery flows `FakeTickService` → `Channel<PriceTick>` → `TickBroadcaster` → SignalR hub → React, where the `Channel<T>` is the seam that a real market feed replaces in Phase 2. Identity is a Development-only middleware resolving a seeded user, so every query is genuinely user-scoped before real auth exists.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core, EF Core 10 (SQL Server), MediatR, FluentValidation, SignalR · React 18, TypeScript strict, Vite, TanStack Query, zod · xUnit, NSubstitute, Testcontainers, Vitest, React Testing Library, MSW · Docker, GitHub Actions

**Spec:** [2026-07-31-walking-skeleton-design.md](../specs/2026-07-31-walking-skeleton-design.md)

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Target framework is `net10.0`.** The SDK installed is 10.0.302. The spec and README were written against .NET 8; Task 11 amends them. Do not create `net8.0` projects.
- **The dependency rule is structural.** `MarketPulse.Domain` must have zero `ProjectReference` and zero third-party `PackageReference` entries. Task 1 adds a test that fails the build if this is violated.
- **Platform is arm64 macOS.** Every SQL Server container must specify `platform: linux/amd64`. Task 0 makes this work.
- **Ticker reference set:** exactly these 25 codes seed the `Tickers` table — `IVV, NDQ, VHY, FANG, VAS, A200, VGS, IOZ, STW, VAP, VAF, IOO, QUAL, ETHI, HACK, ACDC, ASIA, GEAR, MOAT, IHVV, VTS, VEU, SLF, SYI, RBTZ`.
- **Dev user:** `Id = 11111111-1111-1111-1111-111111111111`, `Email = dev@marketpulse.local`. Their watchlist seeds with `IVV, NDQ, VHY, FANG`.
- **Watchlist cap:** maximum 20 items; a ticker may appear at most once.
- **Tick cadence:** one tick per reference ticker per second.
- **Stale threshold:** a cell dims after 10 seconds without a tick.
- **API base path:** `/api/v1`. All errors are RFC 7807 ProblemDetails carrying the correlation ID.
- **Commit style:** Conventional Commits (`feat:`, `test:`, `chore:`, `docs:`).
- **Never commit secrets.** The SA password below is a local-only development credential and must exist only in `docker-compose.yml` and test fixtures, never in application config.

---

## File Structure

| Path | Responsibility |
|---|---|
| `docker-compose.yml` | Local SQL Server 2022 under amd64 emulation |
| `Directory.Packages.props` | Central NuGet version management |
| `MarketPulse.sln` | Solution binding all six .NET projects |
| `src/MarketPulse.Domain/Entities/User.cs` | User entity |
| `src/MarketPulse.Domain/Entities/Ticker.cs` | Reference ticker entity |
| `src/MarketPulse.Domain/Entities/Watchlist.cs` | Aggregate root; owns items, enforces invariants |
| `src/MarketPulse.Domain/Entities/WatchlistItem.cs` | Child entity of the aggregate |
| `src/MarketPulse.Domain/ValueObjects/PriceTick.cs` | Immutable tick record |
| `src/MarketPulse.Domain/Exceptions/DomainException.cs` | Base + per-invariant exceptions |
| `src/MarketPulse.Application/Abstractions/IWatchlistRepository.cs` | Persistence port |
| `src/MarketPulse.Application/Abstractions/ICurrentUser.cs` | Identity port |
| `src/MarketPulse.Application/Watchlists/GetWatchlistQuery.cs` | Query + handler + DTOs |
| `src/MarketPulse.Application/Watchlists/AddWatchlistItemCommand.cs` | Command + handler + validator |
| `src/MarketPulse.Application/Watchlists/RemoveWatchlistItemCommand.cs` | Command + handler |
| `src/MarketPulse.Application/Behaviours/ValidationBehaviour.cs` | MediatR pipeline validation |
| `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs` | EF Core context + configuration |
| `src/MarketPulse.Infrastructure/Persistence/SeedData.cs` | Reference tickers + dev user |
| `src/MarketPulse.Infrastructure/Persistence/DesignTimeDbContextFactory.cs` | Lets `dotnet ef` build the model without the Api project |
| `src/MarketPulse.Infrastructure/Persistence/WatchlistRepository.cs` | Repository implementation |
| `src/MarketPulse.Infrastructure/RealTime/PriceTickChannel.cs` | The swap-seam |
| `src/MarketPulse.Infrastructure/RealTime/FakeTickService.cs` | Synthetic tick producer |
| `src/MarketPulse.Infrastructure/RealTime/RandomWalk.cs` | Pure price-walk function |
| `src/MarketPulse.Api/Controllers/WatchlistController.cs` | REST surface |
| `src/MarketPulse.Api/Hubs/PriceHub.cs` | SignalR hub |
| `src/MarketPulse.Api/RealTime/TickBroadcaster.cs` | Channel consumer → hub fan-out |
| `src/MarketPulse.Api/Middleware/CorrelationIdMiddleware.cs` | Correlation ID per request |
| `src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs` | Domain error → ProblemDetails |
| `src/MarketPulse.Api/Middleware/DevAuthMiddleware.cs` | Development-only identity stub |
| `packages/api-client/src/schemas.ts` | zod schemas — the runtime boundary |
| `packages/api-client/src/client.ts` | Typed fetch wrapper |
| `apps/dashboard/src/features/watchlist/` | Watchlist screen, hooks |
| `apps/dashboard/src/features/prices/streamReducer.ts` | Pure stream-state reducer |
| `apps/dashboard/src/features/prices/usePriceStream.ts` | SignalR binding |
| `.github/workflows/ci.yml` | Build, test, docker build |

---

## Task 0: Environment prerequisites and local SQL Server

**Why first:** SQL Server 2022 ships amd64-only. On this arm64 Mac, Docker falls back to QEMU, and SQL Server segfaults under QEMU (verified: `qemu: uncaught target signal 11`, exit 139). Rosetta is required. Nothing downstream works until this passes.

**Files:**
- Create: `docker-compose.yml`
- Modify: `.gitignore`

**Interfaces:**
- Produces: a SQL Server instance on `localhost:1433`, SA password `Local!Dev!Pass123`, reachable by every later task.

- [ ] **Step 1: Install Rosetta 2**

This needs your machine's admin password and is a one-time system change.

```bash
softwareupdate --install-rosetta --agree-to-license
```

- [ ] **Step 2: Enable Rosetta in Docker Desktop**

Open Docker Desktop → Settings → General → tick **"Use Rosetta for x86_64/amd64 emulation on Apple Silicon"** → Apply & Restart.

Verify it took:

```bash
grep -i rosetta ~/Library/Group\ Containers/group.com.docker/settings-store.json
```

Expected: `"UseVirtualizationFrameworkRosetta": true`

- [ ] **Step 3: Write docker-compose.yml**

```yaml
services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    platform: linux/amd64
    container_name: marketpulse-sql
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "Local!Dev!Pass123"
      MSSQL_PID: Developer
    ports:
      - "1433:1433"
    volumes:
      - mssql-data:/var/opt/mssql
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -C -Q 'SELECT 1' || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 30s

volumes:
  mssql-data:
```

- [ ] **Step 4: Expand .gitignore**

```gitignore
.DS_Store

# .NET
bin/
obj/
*.user

# Node
node_modules/
dist/
.vite/

# Env
.env
.env.local
```

- [ ] **Step 5: Verify SQL Server actually starts and answers**

```bash
docker compose up -d
until docker compose ps sqlserver --format json | grep -q '"Health":"healthy"'; do sleep 5; done
docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Local!Dev!Pass123' -C -Q "SELECT @@VERSION"
```

Expected: prints `Microsoft SQL Server 2022 ...`. **If the container exits 139, Rosetta is not active — return to Step 2.** Do not proceed past this step with a failing database.

- [ ] **Step 6: Commit**

```bash
git add docker-compose.yml .gitignore
git commit -m "chore: add local SQL Server via docker compose"
```

---

## Task 1: Solution skeleton and the dependency rule

**Files:**
- Create: `MarketPulse.sln`, `Directory.Packages.props`, six `.csproj` files
- Test: `tests/MarketPulse.UnitTests/Architecture/DependencyRuleTests.cs`

**Interfaces:**
- Produces: projects `MarketPulse.Domain`, `MarketPulse.Application`, `MarketPulse.Infrastructure`, `MarketPulse.Api`, and test projects `MarketPulse.UnitTests`, `MarketPulse.IntegrationTests`.

- [ ] **Step 1: Create the solution and projects**

```bash
dotnet new sln -n MarketPulse
dotnet new classlib -o src/MarketPulse.Domain -f net10.0
dotnet new classlib -o src/MarketPulse.Application -f net10.0
dotnet new classlib -o src/MarketPulse.Infrastructure -f net10.0
dotnet new webapi -o src/MarketPulse.Api -f net10.0
dotnet new xunit -o tests/MarketPulse.UnitTests -f net10.0
dotnet new xunit -o tests/MarketPulse.IntegrationTests -f net10.0
dotnet sln add \
  src/MarketPulse.Domain/MarketPulse.Domain.csproj \
  src/MarketPulse.Application/MarketPulse.Application.csproj \
  src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj \
  src/MarketPulse.Api/MarketPulse.Api.csproj \
  tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj \
  tests/MarketPulse.IntegrationTests/MarketPulse.IntegrationTests.csproj
```

Paths are listed explicitly rather than globbed — `**` does not expand recursively in a default zsh or bash shell, so `src/**/*.csproj` would silently match nothing.

- [ ] **Step 2: Wire project references (this IS the dependency rule)**

```bash
dotnet add src/MarketPulse.Application reference src/MarketPulse.Domain
dotnet add src/MarketPulse.Infrastructure reference src/MarketPulse.Application
dotnet add src/MarketPulse.Api reference src/MarketPulse.Infrastructure
dotnet add tests/MarketPulse.UnitTests reference src/MarketPulse.Application src/MarketPulse.Domain src/MarketPulse.Infrastructure
dotnet add tests/MarketPulse.IntegrationTests reference src/MarketPulse.Api
```

Note `MarketPulse.Domain` receives no `reference` command. That is the point.

- [ ] **Step 3: Enable central package management and strict settings**

Create `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <!-- Populated by `dotnet add package` in later tasks -->
  </ItemGroup>
</Project>
```

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Write the failing dependency-rule test**

Create `tests/MarketPulse.UnitTests/Architecture/DependencyRuleTests.cs`:

```csharp
using System.Reflection;
using MarketPulse.Domain.Entities;

namespace MarketPulse.UnitTests.Architecture;

public class DependencyRuleTests
{
    [Fact]
    public void Domain_references_nothing_but_the_base_class_library()
    {
        var domain = typeof(Watchlist).Assembly;

        var forbidden = domain.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name =>
                !name.StartsWith("System", StringComparison.Ordinal) &&
                !name.Equals("netstandard", StringComparison.Ordinal) &&
                !name.Equals("mscorlib", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(forbidden);
    }
}
```

- [ ] **Step 5: Run it to confirm it fails**

```bash
dotnet test tests/MarketPulse.UnitTests --filter DependencyRuleTests
```

Expected: FAIL — `Watchlist` does not exist yet (compile error). That is the correct failure; Task 2 creates it.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: scaffold solution with clean architecture project references"
```

---

## Task 2: Domain — the Watchlist aggregate

**Files:**
- Create: `src/MarketPulse.Domain/Entities/{User,Ticker,Watchlist,WatchlistItem}.cs`, `src/MarketPulse.Domain/ValueObjects/PriceTick.cs`, `src/MarketPulse.Domain/Exceptions/DomainException.cs`
- Test: `tests/MarketPulse.UnitTests/Domain/WatchlistTests.cs`

**Interfaces:**
- Produces:
  - `Watchlist.Create(Guid userId) : Watchlist`
  - `Watchlist.AddItem(string ticker) : void` — throws `DuplicateTickerException`, `WatchlistFullException`
  - `Watchlist.RemoveItem(string ticker) : void` — throws `TickerNotOnWatchlistException`
  - `Watchlist.Items : IReadOnlyCollection<WatchlistItem>`
  - `record PriceTick(string Ticker, decimal Price, DateTimeOffset TimestampUtc)`

- [ ] **Step 1: Write the failing tests**

Create `tests/MarketPulse.UnitTests/Domain/WatchlistTests.cs`:

```csharp
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.UnitTests.Domain;

public class WatchlistTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void AddItem_adds_the_ticker()
    {
        var watchlist = Watchlist.Create(UserId);

        watchlist.AddItem("IVV");

        Assert.Contains(watchlist.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public void AddItem_normalises_ticker_to_uppercase()
    {
        var watchlist = Watchlist.Create(UserId);

        watchlist.AddItem("ivv");

        Assert.Contains(watchlist.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public void AddItem_rejects_a_duplicate_ticker()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");

        Assert.Throws<DuplicateTickerException>(() => watchlist.AddItem("IVV"));
    }

    [Fact]
    public void AddItem_rejects_a_duplicate_regardless_of_casing()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");

        Assert.Throws<DuplicateTickerException>(() => watchlist.AddItem("ivv"));
    }

    [Fact]
    public void AddItem_rejects_the_twenty_first_item()
    {
        var watchlist = Watchlist.Create(UserId);
        for (var i = 0; i < 20; i++)
        {
            watchlist.AddItem($"TK{i:D2}");
        }

        Assert.Throws<WatchlistFullException>(() => watchlist.AddItem("EXTRA"));
    }

    [Fact]
    public void RemoveItem_removes_the_ticker()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");

        watchlist.RemoveItem("IVV");

        Assert.Empty(watchlist.Items);
    }

    [Fact]
    public void RemoveItem_rejects_a_ticker_that_is_not_present()
    {
        var watchlist = Watchlist.Create(UserId);

        Assert.Throws<TickerNotOnWatchlistException>(() => watchlist.RemoveItem("IVV"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/MarketPulse.UnitTests --filter WatchlistTests
```

Expected: FAIL — compile errors, types do not exist.

- [ ] **Step 3: Write the exceptions**

Create `src/MarketPulse.Domain/Exceptions/DomainException.cs`:

```csharp
namespace MarketPulse.Domain.Exceptions;

public abstract class DomainException(string message) : Exception(message)
{
    /// <summary>Stable slug used to build the ProblemDetails `type` URI.</summary>
    public abstract string ErrorCode { get; }
}

public sealed class DuplicateTickerException(string ticker)
    : DomainException($"'{ticker}' is already on the watchlist.")
{
    public override string ErrorCode => "duplicate-ticker";
}

public sealed class WatchlistFullException(int max)
    : DomainException($"A watchlist may hold at most {max} items.")
{
    public override string ErrorCode => "watchlist-full";
}

public sealed class TickerNotOnWatchlistException(string ticker)
    : DomainException($"'{ticker}' is not on the watchlist.")
{
    public override string ErrorCode => "ticker-not-on-watchlist";
}
```

- [ ] **Step 4: Write the entities**

Create `src/MarketPulse.Domain/Entities/User.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

public sealed class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;

    private User() { }

    public User(Guid id, string email)
    {
        Id = id;
        Email = email;
    }
}
```

Create `src/MarketPulse.Domain/Entities/Ticker.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

/// <summary>Reference data: the set of ASX ETF codes a watchlist item may name.</summary>
public sealed class Ticker
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public decimal SeedPrice { get; private set; }

    private Ticker() { }

    public Ticker(string code, string name, decimal seedPrice)
    {
        Code = code;
        Name = name;
        SeedPrice = seedPrice;
    }
}
```

Create `src/MarketPulse.Domain/Entities/WatchlistItem.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

public sealed class WatchlistItem
{
    public Guid Id { get; private set; }
    public Guid WatchlistId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public DateTimeOffset AddedUtc { get; private set; }

    private WatchlistItem() { }

    internal WatchlistItem(Guid watchlistId, string ticker)
    {
        Id = Guid.NewGuid();
        WatchlistId = watchlistId;
        Ticker = ticker;
        AddedUtc = DateTimeOffset.UtcNow;
    }
}
```

Create `src/MarketPulse.Domain/Entities/Watchlist.cs`:

```csharp
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Domain.Entities;

public sealed class Watchlist
{
    public const int MaxItems = 20;

    private readonly List<WatchlistItem> _items = [];

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public IReadOnlyCollection<WatchlistItem> Items => _items.AsReadOnly();

    private Watchlist() { }

    public static Watchlist Create(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId
    };

    public void AddItem(string ticker)
    {
        var code = Normalise(ticker);

        if (_items.Count >= MaxItems)
        {
            throw new WatchlistFullException(MaxItems);
        }

        if (_items.Any(i => i.Ticker == code))
        {
            throw new DuplicateTickerException(code);
        }

        _items.Add(new WatchlistItem(Id, code));
    }

    public void RemoveItem(string ticker)
    {
        var code = Normalise(ticker);
        var item = _items.SingleOrDefault(i => i.Ticker == code)
            ?? throw new TickerNotOnWatchlistException(code);

        _items.Remove(item);
    }

    private static string Normalise(string ticker) => ticker.Trim().ToUpperInvariant();
}
```

Note the ordering inside `AddItem`: the capacity check precedes the duplicate check. With 20 items present, adding an existing ticker reports `watchlist-full`. That is deliberate and matches the test above — capacity is the more actionable message.

- [ ] **Step 5: Write the tick record**

Create `src/MarketPulse.Domain/ValueObjects/PriceTick.cs`:

```csharp
namespace MarketPulse.Domain.ValueObjects;

public readonly record struct PriceTick(string Ticker, decimal Price, DateTimeOffset TimestampUtc);
```

- [ ] **Step 6: Run tests — domain and dependency rule both pass now**

```bash
dotnet test tests/MarketPulse.UnitTests
```

Expected: PASS, 8 tests (7 watchlist + 1 dependency rule).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: add Watchlist aggregate with duplicate and capacity invariants"
```

---

## Task 3: Persistence — DbContext, migration, seed

**Files:**
- Create: `src/MarketPulse.Infrastructure/Persistence/{MarketPulseDbContext,SeedData,WatchlistRepository}.cs`, `src/MarketPulse.Application/Abstractions/IWatchlistRepository.cs`
- Test: `tests/MarketPulse.IntegrationTests/{SqlServerFixture,WatchlistPersistenceTests}.cs`

**Interfaces:**
- Consumes: `Watchlist`, `WatchlistItem`, `User`, `Ticker` from Task 2.
- Produces:
  - `IWatchlistRepository.GetForUserAsync(Guid userId, CancellationToken) : Task<Watchlist?>`
  - `IWatchlistRepository.AddAsync(Watchlist, CancellationToken) : Task`
  - `IWatchlistRepository.SaveChangesAsync(CancellationToken) : Task`
  - `IWatchlistRepository.TickerExistsAsync(string code, CancellationToken) : Task<bool>`
  - `SeedData.ReferenceTickers : IReadOnlyList<Ticker>`, `SeedData.DevUserId : Guid`

- [ ] **Step 1: Add packages**

```bash
dotnet add src/MarketPulse.Infrastructure package Microsoft.EntityFrameworkCore.SqlServer
dotnet add src/MarketPulse.Infrastructure package Microsoft.EntityFrameworkCore.Design
dotnet add tests/MarketPulse.IntegrationTests package Testcontainers.MsSql
dotnet add tests/MarketPulse.IntegrationTests package Microsoft.AspNetCore.Mvc.Testing
dotnet tool install --global dotnet-ef
```

`dotnet add package` writes resolved versions into `Directory.Packages.props`. Commit whatever it resolves — do not hand-edit versions.

- [ ] **Step 2: Define the repository port**

Create `src/MarketPulse.Application/Abstractions/IWatchlistRepository.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IWatchlistRepository
{
    Task<Watchlist?> GetForUserAsync(Guid userId, CancellationToken ct);
    Task AddAsync(Watchlist watchlist, CancellationToken ct);
    Task<bool> TickerExistsAsync(string code, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
```

- [ ] **Step 3: Write the seed data**

Create `src/MarketPulse.Infrastructure/Persistence/SeedData.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Infrastructure.Persistence;

public static class SeedData
{
    public static readonly Guid DevUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public const string DevUserEmail = "dev@marketpulse.local";

    /// <summary>Seeded on the dev user's watchlist at first run.</summary>
    public static readonly string[] DefaultWatchlist = ["IVV", "NDQ", "VHY", "FANG"];

    public static readonly IReadOnlyList<Ticker> ReferenceTickers =
    [
        new("IVV",  "iShares S&P 500 ETF",                    62.10m),
        new("NDQ",  "BetaShares NASDAQ 100 ETF",              54.30m),
        new("VHY",  "Vanguard Australian Shares High Yield",  68.75m),
        new("FANG", "Global X FANG+ ETF",                     26.40m),
        new("VAS",  "Vanguard Australian Shares Index",       98.20m),
        new("A200", "BetaShares Australia 200 ETF",          142.60m),
        new("VGS",  "Vanguard MSCI Intl Shares Index",       125.90m),
        new("IOZ",  "iShares Core S&P/ASX 200 ETF",           34.85m),
        new("STW",  "SPDR S&P/ASX 200 Fund",                  74.90m),
        new("VAP",  "Vanguard Australian Property Securities",88.40m),
        new("VAF",  "Vanguard Australian Fixed Interest",     45.15m),
        new("IOO",  "iShares Global 100 ETF",                140.25m),
        new("QUAL", "VanEck MSCI Intl Quality ETF",           48.70m),
        new("ETHI", "BetaShares Global Sustainability",       14.55m),
        new("HACK", "BetaShares Global Cybersecurity ETF",    12.30m),
        new("ACDC", "Global X Battery Tech & Lithium ETF",    72.15m),
        new("ASIA", "BetaShares Asia Technology Tigers ETF",  11.85m),
        new("GEAR", "BetaShares Geared Australian Equity",    32.40m),
        new("MOAT", "VanEck Morningstar Wide Moat ETF",      118.90m),
        new("IHVV", "iShares S&P 500 AUD Hedged ETF",         46.55m),
        new("VTS",  "Vanguard US Total Market Shares",       410.30m),
        new("VEU",  "Vanguard All-World ex-US Shares",        94.20m),
        new("SLF",  "SPDR S&P/ASX 200 Listed Property",       13.75m),
        new("SYI",  "SPDR MSCI Australia Select High Div",    29.60m),
        new("RBTZ", "Global X ROBO Global Robotics ETF",      21.05m)
    ];
}
```

- [ ] **Step 4: Write the DbContext**

Create `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`:

```csharp
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class MarketPulseDbContext(DbContextOptions<MarketPulseDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Ticker> Tickers => Set<Ticker>();
    public DbSet<Watchlist> Watchlists => Set<Watchlist>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Ticker>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(8);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.SeedPrice).HasPrecision(18, 4);
            e.HasData(SeedData.ReferenceTickers);
        });

        b.Entity<Watchlist>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.OwnsMany<WatchlistItem>("_items", items =>
            {
                items.ToTable("WatchlistItems");
                items.WithOwner().HasForeignKey(x => x.WatchlistId);
                items.HasKey(x => x.Id);
                items.Property(x => x.Ticker).HasMaxLength(8).IsRequired();
                items.HasIndex(x => new { x.WatchlistId, x.Ticker }).IsUnique();
            });
        });
    }
}
```

The unique index on `(WatchlistId, Ticker)` is deliberate belt-and-braces: the aggregate already rejects duplicates, and the database refuses them too.

Do **not** add `e.Navigation(x => x.Items).AutoInclude()` — `Items` is a computed property over the `_items` backing field, not a mapped navigation, so that call throws at model build. Owned collections are loaded eagerly by EF Core anyway.

- [ ] **Step 5: Write the repository**

Create `src/MarketPulse.Infrastructure/Persistence/WatchlistRepository.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class WatchlistRepository(MarketPulseDbContext db) : IWatchlistRepository
{
    public Task<Watchlist?> GetForUserAsync(Guid userId, CancellationToken ct) =>
        db.Watchlists.FirstOrDefaultAsync(w => w.UserId == userId, ct);

    public async Task AddAsync(Watchlist watchlist, CancellationToken ct) =>
        await db.Watchlists.AddAsync(watchlist, ct);

    public Task<bool> TickerExistsAsync(string code, CancellationToken ct) =>
        db.Tickers.AnyAsync(t => t.Code == code, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
```

- [ ] **Step 6: Add a design-time factory, then generate the migration**

Without this, `dotnet ef` needs the Api project's DI container, which does not exist until
Task 5 — a forward dependency this removes.

Create `src/MarketPulse.Infrastructure/Persistence/DesignTimeDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MarketPulse.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` at design time. Never resolved at runtime.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MarketPulseDbContext>
{
    public MarketPulseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MarketPulseDbContext>()
            .UseSqlServer("Server=localhost,1433;Database=MarketPulse;User Id=sa;" +
                          "Password=Local!Dev!Pass123;TrustServerCertificate=True")
            .Options;

        return new MarketPulseDbContext(options);
    }
}
```

Then:

```bash
dotnet ef migrations add InitialCreate \
  --project src/MarketPulse.Infrastructure \
  --output-dir Persistence/Migrations
```

Expected: creates `src/MarketPulse.Infrastructure/Persistence/Migrations/*_InitialCreate.cs`
containing `Users`, `Tickers` (with 25 seeded rows), `Watchlists`, and `WatchlistItems`.

- [ ] **Step 7: Write the Testcontainers fixture**

Create `tests/MarketPulse.IntegrationTests/SqlServerFixture.cs`:

```csharp
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace MarketPulse.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public MarketPulseDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MarketPulseDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        return new MarketPulseDbContext(options);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(nameof(SqlServerCollection))]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
```

Testcontainers reads the same Docker socket, so the Rosetta work from Task 0 covers it. No `platform` override is needed — `MsSqlBuilder` requests amd64 itself.

- [ ] **Step 8: Write the failing persistence test**

Create `tests/MarketPulse.IntegrationTests/WatchlistPersistenceTests.cs`:

```csharp
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class WatchlistPersistenceTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Reference_tickers_are_seeded()
    {
        await using var db = fixture.CreateContext();

        Assert.Equal(25, await db.Tickers.CountAsync());
    }

    [Fact]
    public async Task Watchlist_round_trips_with_its_items()
    {
        var userId = Guid.NewGuid();

        await using (var write = fixture.CreateContext())
        {
            write.Users.Add(new User(userId, $"{userId}@test.local"));
            var watchlist = Watchlist.Create(userId);
            watchlist.AddItem("IVV");
            watchlist.AddItem("NDQ");
            await write.Watchlists.AddAsync(watchlist);
            await write.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var loaded = await read.Watchlists.SingleAsync(w => w.UserId == userId);

        Assert.Equal(2, loaded.Items.Count);
        Assert.Contains(loaded.Items, i => i.Ticker == "IVV");
    }
}
```

- [ ] **Step 9: Run it**

```bash
dotnet test tests/MarketPulse.IntegrationTests
```

Expected: PASS. First run pulls nothing (image already cached) but takes ~40s while SQL Server boots.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: add EF Core persistence with seeded reference tickers"
```

---

## Task 4: Application layer — handlers and validation

**Files:**
- Create: `src/MarketPulse.Application/Abstractions/ICurrentUser.cs`, `src/MarketPulse.Application/Watchlists/{GetWatchlistQuery,AddWatchlistItemCommand,RemoveWatchlistItemCommand}.cs`, `src/MarketPulse.Application/Behaviours/ValidationBehaviour.cs`, `src/MarketPulse.Application/DependencyInjection.cs`
- Test: `tests/MarketPulse.UnitTests/Application/AddWatchlistItemHandlerTests.cs`

**Interfaces:**
- Consumes: `IWatchlistRepository` (Task 3), `Watchlist` (Task 2).
- Produces:
  - `ICurrentUser.UserId : Guid`
  - `record GetWatchlistQuery : IRequest<WatchlistDto>`
  - `record WatchlistDto(Guid Id, IReadOnlyList<WatchlistItemDto> Items)`
  - `record WatchlistItemDto(string Ticker, DateTimeOffset AddedUtc)`
  - `record AddWatchlistItemCommand(string Ticker) : IRequest<WatchlistDto>`
  - `record RemoveWatchlistItemCommand(string Ticker) : IRequest<WatchlistDto>`
  - `ApplicationServiceCollectionExtensions.AddApplication(IServiceCollection) : IServiceCollection`

- [ ] **Step 1: Add packages**

```bash
dotnet add src/MarketPulse.Application package MediatR --version 12.*
dotnet add src/MarketPulse.Application package FluentValidation
dotnet add src/MarketPulse.Application package FluentValidation.DependencyInjectionExtensions
dotnet add tests/MarketPulse.UnitTests package NSubstitute
```

MediatR is pinned to 12.x deliberately: 12.x is MIT-licensed, while 13.x moved to a commercial licence. Worth a sentence in ADR-002 — "defend every dependency" is one of the parent README's design principles.

- [ ] **Step 2: Define the identity port**

Create `src/MarketPulse.Application/Abstractions/ICurrentUser.cs`:

```csharp
namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Resolves the caller's identity. Backed by DevAuthMiddleware in slice 1 and
/// by JWT claims from slice 2 onward — no consumer changes when that swap happens.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }
}
```

- [ ] **Step 3: Write the query**

Create `src/MarketPulse.Application/Watchlists/GetWatchlistQuery.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Watchlists;

public record WatchlistItemDto(string Ticker, DateTimeOffset AddedUtc);

public record WatchlistDto(Guid Id, IReadOnlyList<WatchlistItemDto> Items);

public record GetWatchlistQuery : IRequest<WatchlistDto>;

public sealed class GetWatchlistHandler(IWatchlistRepository repo, ICurrentUser user)
    : IRequestHandler<GetWatchlistQuery, WatchlistDto>
{
    public async Task<WatchlistDto> Handle(GetWatchlistQuery request, CancellationToken ct)
    {
        var watchlist = await repo.GetForUserAsync(user.UserId, ct);

        if (watchlist is null)
        {
            watchlist = Watchlist.Create(user.UserId);
            await repo.AddAsync(watchlist, ct);
            await repo.SaveChangesAsync(ct);
        }

        return watchlist.ToDto();
    }
}

internal static class WatchlistMappings
{
    public static WatchlistDto ToDto(this Watchlist w) => new(
        w.Id,
        w.Items.OrderBy(i => i.Ticker)
               .Select(i => new WatchlistItemDto(i.Ticker, i.AddedUtc))
               .ToList());
}
```

- [ ] **Step 4: Write the commands**

Create `src/MarketPulse.Application/Watchlists/AddWatchlistItemCommand.cs`:

```csharp
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Watchlists;

public record AddWatchlistItemCommand(string Ticker) : IRequest<WatchlistDto>;

public sealed class AddWatchlistItemValidator : AbstractValidator<AddWatchlistItemCommand>
{
    public AddWatchlistItemValidator(IWatchlistRepository repo)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithMessage("Ticker is required.")
            .MaximumLength(8).WithMessage("Ticker must be 8 characters or fewer.")
            .MustAsync(async (ticker, ct) =>
                await repo.TickerExistsAsync(ticker.Trim().ToUpperInvariant(), ct))
            .WithMessage(x => $"'{x.Ticker}' is not a known ticker.")
            .WithErrorCode("unknown-ticker");
    }
}

public sealed class AddWatchlistItemHandler(IWatchlistRepository repo, ICurrentUser user)
    : IRequestHandler<AddWatchlistItemCommand, WatchlistDto>
{
    public async Task<WatchlistDto> Handle(AddWatchlistItemCommand request, CancellationToken ct)
    {
        var watchlist = await repo.GetForUserAsync(user.UserId, ct);

        if (watchlist is null)
        {
            watchlist = Watchlist.Create(user.UserId);
            await repo.AddAsync(watchlist, ct);
        }

        watchlist.AddItem(request.Ticker);
        await repo.SaveChangesAsync(ct);

        return watchlist.ToDto();
    }
}
```

Create `src/MarketPulse.Application/Watchlists/RemoveWatchlistItemCommand.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Watchlists;

public record RemoveWatchlistItemCommand(string Ticker) : IRequest<WatchlistDto>;

public sealed class RemoveWatchlistItemHandler(IWatchlistRepository repo, ICurrentUser user)
    : IRequestHandler<RemoveWatchlistItemCommand, WatchlistDto>
{
    public async Task<WatchlistDto> Handle(RemoveWatchlistItemCommand request, CancellationToken ct)
    {
        var watchlist = await repo.GetForUserAsync(user.UserId, ct)
            ?? throw new TickerNotOnWatchlistException(request.Ticker);

        watchlist.RemoveItem(request.Ticker);
        await repo.SaveChangesAsync(ct);

        return watchlist.ToDto();
    }
}
```

- [ ] **Step 5: Write the validation behaviour**

Create `src/MarketPulse.Application/Behaviours/ValidationBehaviour.cs`:

```csharp
using FluentValidation;
using MediatR;

namespace MarketPulse.Application.Behaviours;

public sealed class ValidationBehaviour<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        if (!validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var results = await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, ct)));
        var failures = results.SelectMany(r => r.Errors).Where(f => f is not null).ToList();

        if (failures.Count != 0)
        {
            throw new ValidationException(failures);
        }

        return await next();
    }
}
```

- [ ] **Step 6: Write the DI registration**

Create `src/MarketPulse.Application/DependencyInjection.cs`:

```csharp
using System.Reflection;
using FluentValidation;
using MarketPulse.Application.Behaviours;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace MarketPulse.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddMediatR(c => c.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(assembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));

        return services;
    }
}
```

- [ ] **Step 7: Write the handler test**

Create `tests/MarketPulse.UnitTests/Application/AddWatchlistItemHandlerTests.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Watchlists;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class AddWatchlistItemHandlerTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static (AddWatchlistItemHandler Handler, IWatchlistRepository Repo) Build(Watchlist? existing)
    {
        var repo = Substitute.For<IWatchlistRepository>();
        repo.GetForUserAsync(UserId, Arg.Any<CancellationToken>()).Returns(existing);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        return (new AddWatchlistItemHandler(repo, user), repo);
    }

    [Fact]
    public async Task Adds_the_ticker_and_saves()
    {
        var watchlist = Watchlist.Create(UserId);
        var (handler, repo) = Build(watchlist);

        var result = await handler.Handle(new AddWatchlistItemCommand("IVV"), CancellationToken.None);

        Assert.Contains(result.Items, i => i.Ticker == "IVV");
        await repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creates_a_watchlist_when_the_user_has_none()
    {
        var (handler, repo) = Build(existing: null);

        await handler.Handle(new AddWatchlistItemCommand("IVV"), CancellationToken.None);

        await repo.Received(1).AddAsync(Arg.Any<Watchlist>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Propagates_the_duplicate_invariant()
    {
        var watchlist = Watchlist.Create(UserId);
        watchlist.AddItem("IVV");
        var (handler, _) = Build(watchlist);

        await Assert.ThrowsAsync<DuplicateTickerException>(
            () => handler.Handle(new AddWatchlistItemCommand("IVV"), CancellationToken.None));
    }
}
```

- [ ] **Step 8: Run the tests**

```bash
dotnet test tests/MarketPulse.UnitTests
```

Expected: PASS, 11 tests.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: add MediatR handlers and validation for watchlist operations"
```

---

## Task 5: API — controllers, middleware, ProblemDetails

**Files:**
- Create: `src/MarketPulse.Api/Controllers/WatchlistController.cs`, `src/MarketPulse.Api/Middleware/{CorrelationIdMiddleware,ExceptionHandlingMiddleware,DevAuthMiddleware}.cs`, `src/MarketPulse.Api/CurrentUser.cs`, `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Modify: `src/MarketPulse.Api/Program.cs`, `src/MarketPulse.Api/appsettings.Development.json`
- Test: `tests/MarketPulse.IntegrationTests/WatchlistApiTests.cs`

**Interfaces:**
- Consumes: `AddWatchlistItemCommand`, `RemoveWatchlistItemCommand`, `GetWatchlistQuery`, `WatchlistDto` (Task 4).
- Produces: `GET /api/v1/watchlist`, `POST /api/v1/watchlist/items`, `DELETE /api/v1/watchlist/items/{ticker}`; header `X-Correlation-Id` on every response; `InfrastructureServiceCollectionExtensions.AddInfrastructure(IServiceCollection, string connectionString)`.

- [ ] **Step 1: Write the Infrastructure DI registration**

Create `src/MarketPulse.Infrastructure/DependencyInjection.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarketPulse.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<MarketPulseDbContext>(o => o.UseSqlServer(connectionString));
        services.AddScoped<IWatchlistRepository, WatchlistRepository>();
        return services;
    }
}
```

- [ ] **Step 2: Write Program.cs**

Replace `src/MarketPulse.Api/Program.cs` entirely:

```csharp
using MarketPulse.Api.Middleware;
using MarketPulse.Api;
using MarketPulse.Application;
using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MarketPulse")
    ?? throw new InvalidOperationException("ConnectionStrings:MarketPulse is not configured.");

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.UseMiddleware<DevAuthMiddleware>();
}

app.MapControllers();

app.Run();

public partial class Program;
```

`public partial class Program;` is what lets `WebApplicationFactory<Program>` find the entry point from the test project.

- [ ] **Step 3: Configure the connection string**

Replace `src/MarketPulse.Api/appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ConnectionStrings": {
    "MarketPulse": "Server=localhost,1433;Database=MarketPulse;User Id=sa;Password=Local!Dev!Pass123;TrustServerCertificate=True"
  }
}
```

- [ ] **Step 4: Write the middleware**

Create `src/MarketPulse.Api/Middleware/CorrelationIdMiddleware.cs`:

```csharp
namespace MarketPulse.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var supplied)
            && !string.IsNullOrWhiteSpace(supplied)
                ? supplied.ToString()
                : Guid.NewGuid().ToString();

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        await next(context);
    }
}
```

Create `src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs`:

```csharp
using FluentValidation;
using MarketPulse.Domain.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (DomainException ex)
        {
            await WriteAsync(context, StatusCodes.Status409Conflict, ex.ErrorCode, ex.Message);
        }
        catch (ValidationException ex)
        {
            var code = ex.Errors.FirstOrDefault()?.ErrorCode ?? "validation-failed";
            var detail = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
            await WriteAsync(context, StatusCodes.Status400BadRequest, code, detail);
        }
        catch (Exception ex)
        {
            var correlationId = CorrelationId(context);
            logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                "internal-error", "An unexpected error occurred.");
        }
    }

    private static string CorrelationId(HttpContext context) =>
        context.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? "unknown";

    private static async Task WriteAsync(
        HttpContext context, int status, string errorCode, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = errorCode,
            Type = $"https://marketpulse.local/errors/{errorCode}",
            Detail = detail
        };
        problem.Extensions["correlationId"] = CorrelationId(context);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    }
}
```

Create `src/MarketPulse.Api/Middleware/DevAuthMiddleware.cs`:

```csharp
using System.Security.Claims;
using MarketPulse.Infrastructure.Persistence;

namespace MarketPulse.Api.Middleware;

/// <summary>
/// Development-only identity stub. Slice 2 deletes this file and issues real JWTs;
/// no schema, query, or handler changes are needed when it goes.
/// </summary>
public sealed class DevAuthMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DevAuthMiddleware must never run outside the Development environment.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, SeedData.DevUserId.ToString())],
            authenticationType: "DevAuth");

        context.User = new ClaimsPrincipal(identity);

        await next(context);
    }
}
```

The environment check is defence in depth: `Program.cs` already registers this only in Development, and the middleware refuses to run anywhere else even if that registration is changed by mistake.

- [ ] **Step 5: Write the ICurrentUser adapter**

Create `src/MarketPulse.Api/CurrentUser.cs`:

```csharp
using System.Security.Claims;
using MarketPulse.Application.Abstractions;

namespace MarketPulse.Api;

public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid UserId
    {
        get
        {
            var raw = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id)
                ? id
                : throw new UnauthorizedAccessException("No authenticated user on the request.");
        }
    }
}
```

- [ ] **Step 6: Write the controller**

Create `src/MarketPulse.Api/Controllers/WatchlistController.cs`:

```csharp
using MarketPulse.Application.Watchlists;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Route("api/v1/watchlist")]
public sealed class WatchlistController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<WatchlistDto>> Get(CancellationToken ct) =>
        Ok(await sender.Send(new GetWatchlistQuery(), ct));

    [HttpPost("items")]
    public async Task<ActionResult<WatchlistDto>> AddItem(
        [FromBody] AddWatchlistItemCommand command, CancellationToken ct) =>
        Ok(await sender.Send(command, ct));

    [HttpDelete("items/{ticker}")]
    public async Task<ActionResult<WatchlistDto>> RemoveItem(string ticker, CancellationToken ct) =>
        Ok(await sender.Send(new RemoveWatchlistItemCommand(ticker), ct));
}
```

- [ ] **Step 7: Delete the webapi template's sample code**

`dotnet new webapi` scaffolds a weather-forecast sample that will not compile against the
rewritten `Program.cs`.

```bash
rm -f src/MarketPulse.Api/MarketPulse.Api.http
grep -rl "WeatherForecast" src/MarketPulse.Api/ | xargs -r rm -f
```

- [ ] **Step 8: Write the API integration test**

Create `tests/MarketPulse.IntegrationTests/WatchlistApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using MarketPulse.Application.Watchlists;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class WatchlistApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
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

        _client = _factory.CreateClient();

        await using var db = fixture.CreateContext();
        if (!await db.Users.AnyAsync(u => u.Id == SeedData.DevUserId))
        {
            db.Users.Add(new User(SeedData.DevUserId, SeedData.DevUserEmail));
            await db.SaveChangesAsync();
        }

        var existing = await db.Watchlists
            .FirstOrDefaultAsync(w => w.UserId == SeedData.DevUserId);
        if (existing is not null)
        {
            db.Watchlists.Remove(existing);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Post_then_get_then_delete_round_trips()
    {
        var added = await _client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));
        added.EnsureSuccessStatusCode();

        var list = await _client.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");
        Assert.NotNull(list);
        Assert.Contains(list!.Items, i => i.Ticker == "IVV");

        var removed = await _client.DeleteAsync("/api/v1/watchlist/items/IVV");
        removed.EnsureSuccessStatusCode();

        var after = await _client.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");
        Assert.DoesNotContain(after!.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public async Task Duplicate_ticker_returns_409_problem_details()
    {
        await _client.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("NDQ"));

        var second = await _client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("NDQ"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

        var body = await second.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("duplicate-ticker", body!["title"].ToString());
        Assert.True(body.ContainsKey("correlationId"));
    }

    [Fact]
    public async Task Unknown_ticker_returns_400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("NOTREAL"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_a_correlation_id()
    {
        var response = await _client.GetAsync("/api/v1/watchlist");

        Assert.True(response.Headers.Contains("X-Correlation-Id"));
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 9: Run the tests**

```bash
dotnet test tests/MarketPulse.IntegrationTests
```

Expected: PASS, 6 tests.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: expose watchlist REST API with ProblemDetails error mapping"
```

---

## Task 6: Real-time — channel, producer, broadcaster, hub

**Files:**
- Create: `src/MarketPulse.Infrastructure/RealTime/{PriceTickChannel,RandomWalk,FakeTickService}.cs`, `src/MarketPulse.Api/Hubs/PriceHub.cs`, `src/MarketPulse.Api/RealTime/TickBroadcaster.cs`
- Modify: `src/MarketPulse.Api/Program.cs`, `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Test: `tests/MarketPulse.UnitTests/RealTime/RandomWalkTests.cs`, `tests/MarketPulse.IntegrationTests/PriceStreamTests.cs`

**Interfaces:**
- Consumes: `PriceTick` (Task 2), `SeedData.ReferenceTickers` (Task 3).
- Produces:
  - `PriceTickChannel.Writer : ChannelWriter<PriceTick>`, `PriceTickChannel.Reader : ChannelReader<PriceTick>`
  - `RandomWalk.Next(decimal current, Random rng) : decimal`
  - SignalR hub at `/hubs/prices` emitting method `tick` with payload `{ ticker, price, timestampUtc }`

- [ ] **Step 1: Write the failing random-walk test**

Create `tests/MarketPulse.UnitTests/RealTime/RandomWalkTests.cs`:

```csharp
using MarketPulse.Infrastructure.RealTime;

namespace MarketPulse.UnitTests.RealTime;

public class RandomWalkTests
{
    [Fact]
    public void Next_stays_within_one_percent_of_the_current_price()
    {
        var rng = new Random(Seed: 42);
        var current = 100m;

        for (var i = 0; i < 1000; i++)
        {
            var next = RandomWalk.Next(current, rng);

            Assert.InRange(next, current * 0.99m, current * 1.01m);
            current = next;
        }
    }

    [Fact]
    public void Next_never_returns_a_non_positive_price()
    {
        var rng = new Random(Seed: 7);
        var current = 0.02m;

        for (var i = 0; i < 500; i++)
        {
            current = RandomWalk.Next(current, rng);
            Assert.True(current > 0m);
        }
    }

    [Fact]
    public void Next_rounds_to_two_decimal_places()
    {
        var rng = new Random(Seed: 1);

        var next = RandomWalk.Next(62.10m, rng);

        Assert.Equal(next, Math.Round(next, 2));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/MarketPulse.UnitTests --filter RandomWalkTests
```

Expected: FAIL — `RandomWalk` does not exist.

- [ ] **Step 3: Implement the random walk**

Create `src/MarketPulse.Infrastructure/RealTime/RandomWalk.cs`:

```csharp
namespace MarketPulse.Infrastructure.RealTime;

public static class RandomWalk
{
    private const decimal MaxMovePercent = 0.01m;
    private const decimal Floor = 0.01m;

    /// <summary>Moves a price by at most ±1%, rounded to cents, never below one cent.</summary>
    public static decimal Next(decimal current, Random rng)
    {
        var drift = ((decimal)rng.NextDouble() * 2m - 1m) * MaxMovePercent;
        var candidate = Math.Round(current * (1m + drift), 2, MidpointRounding.AwayFromZero);

        if (candidate < Floor)
        {
            return Floor;
        }

        return candidate > current * (1m + MaxMovePercent)
            ? Math.Round(current * (1m + MaxMovePercent), 2, MidpointRounding.ToZero)
            : candidate;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/MarketPulse.UnitTests --filter RandomWalkTests
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Write the channel — the swap-seam**

Create `src/MarketPulse.Infrastructure/RealTime/PriceTickChannel.cs`:

```csharp
using System.Threading.Channels;
using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Infrastructure.RealTime;

/// <summary>
/// The seam between tick production and tick delivery. Slice 1 fills it with
/// FakeTickService; Phase 2 replaces only the producer, leaving every consumer intact.
/// </summary>
public sealed class PriceTickChannel
{
    private readonly Channel<PriceTick> _channel =
        Channel.CreateBounded<PriceTick>(new BoundedChannelOptions(capacity: 1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelWriter<PriceTick> Writer => _channel.Writer;
    public ChannelReader<PriceTick> Reader => _channel.Reader;
}
```

`DropOldest` is the right policy for market data: a stale price is worth less than a fresh one, so back-pressure must never stall the producer.

- [ ] **Step 6: Write the producer**

Create `src/MarketPulse.Infrastructure/RealTime/FakeTickService.cs`:

```csharp
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketPulse.Infrastructure.RealTime;

public sealed class FakeTickService(
    PriceTickChannel channel,
    ILogger<FakeTickService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rng = new Random();
        var prices = SeedData.ReferenceTickers.ToDictionary(t => t.Code, t => t.SeedPrice);

        logger.LogInformation("FakeTickService started for {Count} tickers.", prices.Count);

        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var code in prices.Keys.ToArray())
                {
                    prices[code] = RandomWalk.Next(prices[code], rng);
                    await channel.Writer.WriteAsync(
                        new PriceTick(code, prices[code], DateTimeOffset.UtcNow),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("FakeTickService stopping.");
        }
    }
}
```

- [ ] **Step 7: Write the hub and broadcaster**

Create `src/MarketPulse.Api/Hubs/PriceHub.cs`:

```csharp
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.Hubs;

public sealed class PriceHub : Hub;
```

Create `src/MarketPulse.Api/RealTime/TickBroadcaster.cs`:

```csharp
using MarketPulse.Api.Hubs;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.RealTime;

public sealed class TickBroadcaster(
    PriceTickChannel channel,
    IHubContext<PriceHub> hub,
    ILogger<TickBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(stoppingToken))
            {
                await hub.Clients.All.SendAsync(
                    "tick",
                    new { ticker = tick.Ticker, price = tick.Price, timestampUtc = tick.TimestampUtc },
                    stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickBroadcaster stopping.");
        }
    }
}
```

- [ ] **Step 8: Register everything**

In `src/MarketPulse.Infrastructure/DependencyInjection.cs`, add inside `AddInfrastructure` before `return services;`:

```csharp
        services.AddSingleton<PriceTickChannel>();
        services.AddHostedService<FakeTickService>();
```

Add the matching `using MarketPulse.Infrastructure.RealTime;` at the top.

In `src/MarketPulse.Api/Program.cs`, add after `builder.Services.AddProblemDetails();`:

```csharp
builder.Services.AddSignalR();
builder.Services.AddHostedService<TickBroadcaster>();
```

and after `app.MapControllers();`:

```csharp
app.MapHub<PriceHub>("/hubs/prices");
```

with `using MarketPulse.Api.Hubs;` and `using MarketPulse.Api.RealTime;` at the top.

- [ ] **Step 9: Write the end-to-end stream test**

Create `tests/MarketPulse.IntegrationTests/PriceStreamTests.cs`:

```csharp
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PriceStreamTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task A_tick_reaches_a_connected_client_within_five_seconds()
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

        var received = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<TickPayload>("tick", payload => received.TrySetResult(payload.Ticker));

        await connection.StartAsync();

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        await connection.DisposeAsync();

        Assert.Same(received.Task, completed);
        Assert.False(string.IsNullOrWhiteSpace(received.Task.Result));
    }

    private sealed record TickPayload(string Ticker, decimal Price, DateTimeOffset TimestampUtc);
}
```

Add the SignalR client package first:

```bash
dotnet add tests/MarketPulse.IntegrationTests package Microsoft.AspNetCore.SignalR.Client
```

This is the one place a transport test earns its keep — it proves the whole channel → broadcaster → hub → client chain, not SignalR itself.

- [ ] **Step 10: Run the tests**

```bash
dotnet test
```

Expected: PASS, 21 tests total.

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat: stream synthetic price ticks over SignalR via a bounded channel"
```

---

## Task 7: Frontend workspace and the typed API client

**Files:**
- Create: `package.json`, `pnpm-workspace.yaml`, `packages/api-client/{package.json,tsconfig.json,src/schemas.ts,src/client.ts,src/index.ts}`, `packages/api-client/src/schemas.test.ts`, `vitest.config.ts`

**Interfaces:**
- Produces:
  - `watchlistSchema : ZodType<Watchlist>`, `tickSchema : ZodType<Tick>`
  - `type Watchlist = { id: string; items: { ticker: string; addedUtc: string }[] }`
  - `type Tick = { ticker: string; price: number; timestampUtc: string }`
  - `createApiClient(baseUrl: string)` returning `{ getWatchlist, addItem, removeItem }`
  - `class ApiError extends Error { status: number; errorCode: string; correlationId?: string }`

- [ ] **Step 1: Create the workspace**

`package.json`:

```json
{
  "name": "marketpulse-pro",
  "private": true,
  "packageManager": "pnpm@11.1.1",
  "scripts": {
    "lint": "pnpm -r lint",
    "typecheck": "pnpm -r typecheck",
    "test": "pnpm -r test"
  }
}
```

`pnpm-workspace.yaml`:

```yaml
packages:
  - "apps/*"
  - "packages/*"
```

- [ ] **Step 2: Create the api-client package**

`packages/api-client/package.json`:

```json
{
  "name": "@marketpulse/api-client",
  "version": "0.1.0",
  "type": "module",
  "main": "./src/index.ts",
  "types": "./src/index.ts",
  "scripts": {
    "typecheck": "tsc --noEmit",
    "test": "vitest run",
    "lint": "tsc --noEmit"
  },
  "dependencies": {
    "zod": "^3.23.8"
  },
  "devDependencies": {
    "typescript": "^5.6.0",
    "vitest": "^2.1.0"
  }
}
```

`packages/api-client/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "skipLibCheck": true,
    "noEmit": true,
    "types": ["vitest/globals"]
  },
  "include": ["src"]
}
```

- [ ] **Step 3: Write the failing schema test**

`packages/api-client/src/schemas.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { tickSchema, watchlistSchema } from './schemas';

describe('watchlistSchema', () => {
  it('accepts a well-formed watchlist', () => {
    const parsed = watchlistSchema.parse({
      id: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
      items: [{ ticker: 'IVV', addedUtc: '2026-07-31T00:00:00+00:00' }],
    });

    expect(parsed.items[0]?.ticker).toBe('IVV');
  });

  it('rejects a payload missing items', () => {
    expect(() => watchlistSchema.parse({ id: 'x' })).toThrow();
  });
});

describe('tickSchema', () => {
  it('accepts a well-formed tick', () => {
    const parsed = tickSchema.parse({
      ticker: 'NDQ',
      price: 54.31,
      timestampUtc: '2026-07-31T00:00:01+00:00',
    });

    expect(parsed.price).toBeCloseTo(54.31);
  });

  it('rejects a tick whose price is a string', () => {
    expect(() =>
      tickSchema.parse({ ticker: 'NDQ', price: '54.31', timestampUtc: 'x' }),
    ).toThrow();
  });
});
```

- [ ] **Step 4: Install and run to verify failure**

```bash
pnpm install
pnpm --filter @marketpulse/api-client test
```

Expected: FAIL — `./schemas` does not exist.

- [ ] **Step 5: Write the schemas**

`packages/api-client/src/schemas.ts`:

```ts
import { z } from 'zod';

export const watchlistItemSchema = z.object({
  ticker: z.string().min(1).max(8),
  addedUtc: z.string(),
});

export const watchlistSchema = z.object({
  id: z.string(),
  items: z.array(watchlistItemSchema),
});

export const tickSchema = z.object({
  ticker: z.string().min(1).max(8),
  price: z.number().positive(),
  timestampUtc: z.string(),
});

export const problemDetailsSchema = z.object({
  title: z.string(),
  status: z.number(),
  detail: z.string().optional(),
  correlationId: z.string().optional(),
});

export type WatchlistItem = z.infer<typeof watchlistItemSchema>;
export type Watchlist = z.infer<typeof watchlistSchema>;
export type Tick = z.infer<typeof tickSchema>;
export type ProblemDetails = z.infer<typeof problemDetailsSchema>;
```

- [ ] **Step 6: Write the client**

`packages/api-client/src/client.ts`:

```ts
import { problemDetailsSchema, watchlistSchema, type Watchlist } from './schemas';

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

async function request<T>(
  url: string,
  init: RequestInit,
  parse: (data: unknown) => T,
): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init.headers },
  });

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

export function createApiClient(baseUrl: string) {
  const root = baseUrl.replace(/\/$/, '');

  return {
    getWatchlist: (signal?: AbortSignal): Promise<Watchlist> =>
      request(`${root}/api/v1/watchlist`, { method: 'GET', signal }, (d) =>
        watchlistSchema.parse(d),
      ),

    addItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        `${root}/api/v1/watchlist/items`,
        { method: 'POST', body: JSON.stringify({ ticker }), signal },
        (d) => watchlistSchema.parse(d),
      ),

    removeItem: (ticker: string, signal?: AbortSignal): Promise<Watchlist> =>
      request(
        `${root}/api/v1/watchlist/items/${encodeURIComponent(ticker)}`,
        { method: 'DELETE', signal },
        (d) => watchlistSchema.parse(d),
      ),
  };
}

export type ApiClient = ReturnType<typeof createApiClient>;
```

`packages/api-client/src/index.ts`:

```ts
export * from './schemas';
export * from './client';
```

- [ ] **Step 7: Run the tests**

```bash
pnpm --filter @marketpulse/api-client test
```

Expected: PASS, 4 tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: add zod-validated typed API client package"
```

---

## Task 8: Dashboard — watchlist screen

**Files:**
- Create: `apps/dashboard/{package.json,tsconfig.json,vite.config.ts,index.html}`, `apps/dashboard/src/{main.tsx,App.tsx,api.ts}`, `apps/dashboard/src/features/watchlist/{useWatchlist.ts,WatchlistScreen.tsx,WatchlistScreen.test.tsx}`, `apps/dashboard/src/test/setup.ts`

**Interfaces:**
- Consumes: `createApiClient`, `ApiError`, `Watchlist` (Task 7).
- Produces: `apiClient` singleton, `useWatchlist()`, `useAddItem()`, `useRemoveItem()`, `<WatchlistScreen />`.

- [ ] **Step 1: Scaffold the app**

`apps/dashboard/package.json`:

```json
{
  "name": "@marketpulse/dashboard",
  "private": true,
  "type": "module",
  "scripts": {
    "dev": "vite",
    "build": "tsc --noEmit && vite build",
    "typecheck": "tsc --noEmit",
    "lint": "tsc --noEmit",
    "test": "vitest run"
  },
  "dependencies": {
    "@marketpulse/api-client": "workspace:*",
    "@microsoft/signalr": "^8.0.7",
    "@tanstack/react-query": "^5.59.0",
    "react": "^18.3.1",
    "react-dom": "^18.3.1"
  },
  "devDependencies": {
    "@testing-library/jest-dom": "^6.5.0",
    "@testing-library/react": "^16.0.1",
    "@testing-library/user-event": "^14.5.2",
    "@types/react": "^18.3.11",
    "@types/react-dom": "^18.3.1",
    "@vitejs/plugin-react": "^4.3.2",
    "jsdom": "^25.0.1",
    "msw": "^2.4.9",
    "typescript": "^5.6.0",
    "vite": "^5.4.8",
    "vitest": "^2.1.0"
  }
}
```

`apps/dashboard/vite.config.ts`:

```ts
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  server: { port: 5173 },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
  },
});
```

`apps/dashboard/tsconfig.json`:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "lib": ["ES2022", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "bundler",
    "jsx": "react-jsx",
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "skipLibCheck": true,
    "noEmit": true,
    "types": ["vitest/globals", "@testing-library/jest-dom"]
  },
  "include": ["src", "vite.config.ts"]
}
```

`apps/dashboard/index.html`:

```html
<!doctype html>
<html lang="en-AU">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>MarketPulse Pro</title>
  </head>
  <body>
    <div id="root"></div>
    <script type="module" src="/src/main.tsx"></script>
  </body>
</html>
```

`apps/dashboard/src/test/setup.ts`:

```ts
import '@testing-library/jest-dom/vitest';
```

- [ ] **Step 2: Wire the API client singleton**

`apps/dashboard/src/api.ts`:

```ts
import { createApiClient } from '@marketpulse/api-client';

export const API_BASE_URL = import.meta.env['VITE_API_URL'] ?? 'http://localhost:5100';

export const apiClient = createApiClient(API_BASE_URL);
```

- [ ] **Step 3: Write the failing screen test**

`apps/dashboard/src/features/watchlist/WatchlistScreen.test.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HttpResponse, http } from 'msw';
import { setupServer } from 'msw/node';
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { WatchlistScreen } from './WatchlistScreen';

const server = setupServer(
  http.get('http://localhost:5100/api/v1/watchlist', () =>
    HttpResponse.json({
      id: 'w1',
      items: [{ ticker: 'IVV', addedUtc: '2026-07-31T00:00:00+00:00' }],
    }),
  ),
);

beforeAll(() => server.listen({ onUnhandledRequest: 'error' }));
afterEach(() => server.resetHandlers());
afterAll(() => server.close());

function renderScreen() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <WatchlistScreen />
    </QueryClientProvider>,
  );
}

describe('WatchlistScreen', () => {
  it('renders the tickers returned by the API', async () => {
    renderScreen();

    expect(await screen.findByText('IVV')).toBeInTheDocument();
  });

  it('shows the server message when adding a duplicate', async () => {
    server.use(
      http.post('http://localhost:5100/api/v1/watchlist/items', () =>
        HttpResponse.json(
          {
            title: 'duplicate-ticker',
            status: 409,
            detail: "'IVV' is already on the watchlist.",
            correlationId: 'abc-123',
          },
          { status: 409 },
        ),
      ),
    );

    renderScreen();
    await screen.findByText('IVV');

    await userEvent.type(screen.getByLabelText(/add ticker/i), 'IVV');
    await userEvent.click(screen.getByRole('button', { name: /add/i }));

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(
        "'IVV' is already on the watchlist.",
      ),
    );
  });
});
```

- [ ] **Step 4: Run to verify failure**

```bash
pnpm install
pnpm --filter @marketpulse/dashboard test
```

Expected: FAIL — `./WatchlistScreen` does not exist.

- [ ] **Step 5: Write the hooks**

`apps/dashboard/src/features/watchlist/useWatchlist.ts`:

```ts
import { ApiError, type Watchlist } from '@marketpulse/api-client';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../../api';

const watchlistKey = ['watchlist'] as const;

export function useWatchlist() {
  return useQuery({
    queryKey: watchlistKey,
    queryFn: ({ signal }) => apiClient.getWatchlist(signal),
  });
}

export function useAddItem() {
  const queryClient = useQueryClient();

  return useMutation<Watchlist, ApiError, string>({
    mutationFn: (ticker) => apiClient.addItem(ticker),
    onSuccess: (watchlist) => queryClient.setQueryData(watchlistKey, watchlist),
  });
}

export function useRemoveItem() {
  const queryClient = useQueryClient();

  return useMutation<Watchlist, ApiError, string>({
    mutationFn: (ticker) => apiClient.removeItem(ticker),
    onSuccess: (watchlist) => queryClient.setQueryData(watchlistKey, watchlist),
  });
}
```

- [ ] **Step 6: Write the screen**

`apps/dashboard/src/features/watchlist/WatchlistScreen.tsx`:

```tsx
import { useState, type FormEvent } from 'react';
import { useAddItem, useRemoveItem, useWatchlist } from './useWatchlist';

export function WatchlistScreen() {
  const { data, isPending, isError } = useWatchlist();
  const addItem = useAddItem();
  const removeItem = useRemoveItem();
  const [ticker, setTicker] = useState('');

  function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const value = ticker.trim();
    if (value === '') return;

    addItem.mutate(value, { onSuccess: () => setTicker('') });
  }

  if (isPending) return <p>Loading watchlist…</p>;
  if (isError) return <p role="alert">Could not load your watchlist.</p>;

  return (
    <section aria-labelledby="watchlist-heading">
      <h2 id="watchlist-heading">Watchlist</h2>

      <form onSubmit={handleSubmit}>
        <label htmlFor="add-ticker">Add ticker</label>
        <input
          id="add-ticker"
          value={ticker}
          onChange={(e) => setTicker(e.target.value)}
          maxLength={8}
        />
        <button type="submit" disabled={addItem.isPending}>
          Add
        </button>
      </form>

      {addItem.isError && (
        <p role="alert">
          {addItem.error.message}
          {addItem.error.correlationId && ` (ref: ${addItem.error.correlationId})`}
        </p>
      )}

      <ul>
        {data.items.map((item) => (
          <li key={item.ticker}>
            <span>{item.ticker}</span>
            <button
              type="button"
              onClick={() => removeItem.mutate(item.ticker)}
              aria-label={`Remove ${item.ticker}`}
            >
              Remove
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
```

- [ ] **Step 7: Write the app shell**

`apps/dashboard/src/App.tsx`:

```tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { WatchlistScreen } from './features/watchlist/WatchlistScreen';

const queryClient = new QueryClient();

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <header>
        <h1>MarketPulse Pro</h1>
      </header>
      <main>
        <WatchlistScreen />
      </main>
    </QueryClientProvider>
  );
}
```

`apps/dashboard/src/main.tsx`:

```tsx
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
```

- [ ] **Step 8: Run the tests**

```bash
pnpm --filter @marketpulse/dashboard test
```

Expected: PASS, 2 tests.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: add watchlist screen with TanStack Query and MSW tests"
```

---

## Task 9: Dashboard — live price stream

**Files:**
- Create: `apps/dashboard/src/features/prices/{streamReducer.ts,streamReducer.test.ts,usePriceStream.ts,PriceCell.tsx}`
- Modify: `apps/dashboard/src/features/watchlist/WatchlistScreen.tsx`

**Interfaces:**
- Consumes: `Tick`, `tickSchema` (Task 7); hub at `/hubs/prices` method `tick` (Task 6).
- Produces:
  - `type StreamState = { status: 'connecting' | 'connected' | 'reconnecting'; prices: Record<string, { price: number; receivedAt: number }> }`
  - `streamReducer(state, action) : StreamState`
  - `usePriceStream() : { status, prices, isStale(ticker, now) }`

- [ ] **Step 1: Write the failing reducer tests**

`apps/dashboard/src/features/prices/streamReducer.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { initialStreamState, isStale, streamReducer, STALE_AFTER_MS } from './streamReducer';

describe('streamReducer', () => {
  it('starts in the connecting state with no prices', () => {
    expect(initialStreamState.status).toBe('connecting');
    expect(initialStreamState.prices).toEqual({});
  });

  it('records a tick against its ticker', () => {
    const next = streamReducer(initialStreamState, {
      type: 'tick',
      ticker: 'IVV',
      price: 62.4,
      receivedAt: 1000,
    });

    expect(next.prices['IVV']).toEqual({ price: 62.4, receivedAt: 1000 });
  });

  it('overwrites an earlier price for the same ticker', () => {
    const first = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });
    const second = streamReducer(first, {
      type: 'tick', ticker: 'IVV', price: 62.9, receivedAt: 2000,
    });

    expect(second.prices['IVV']?.price).toBe(62.9);
  });

  it('transitions to reconnecting but keeps the last known prices', () => {
    const withTick = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    const dropped = streamReducer(withTick, { type: 'reconnecting' });

    expect(dropped.status).toBe('reconnecting');
    expect(dropped.prices['IVV']?.price).toBe(62.4);
  });

  it('transitions back to connected', () => {
    const dropped = streamReducer(initialStreamState, { type: 'reconnecting' });

    expect(streamReducer(dropped, { type: 'connected' }).status).toBe('connected');
  });
});

describe('isStale', () => {
  it('is false immediately after a tick', () => {
    const state = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    expect(isStale(state, 'IVV', 1000 + STALE_AFTER_MS - 1)).toBe(false);
  });

  it('is true once the stale threshold elapses', () => {
    const state = streamReducer(initialStreamState, {
      type: 'tick', ticker: 'IVV', price: 62.4, receivedAt: 1000,
    });

    expect(isStale(state, 'IVV', 1000 + STALE_AFTER_MS)).toBe(true);
  });

  it('treats an unseen ticker as stale', () => {
    expect(isStale(initialStreamState, 'NDQ', 5000)).toBe(true);
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
pnpm --filter @marketpulse/dashboard test streamReducer
```

Expected: FAIL — `./streamReducer` does not exist.

- [ ] **Step 3: Write the reducer**

`apps/dashboard/src/features/prices/streamReducer.ts`:

```ts
export const STALE_AFTER_MS = 10_000;

export type StreamStatus = 'connecting' | 'connected' | 'reconnecting';

export interface StreamState {
  status: StreamStatus;
  prices: Record<string, { price: number; receivedAt: number }>;
}

export type StreamAction =
  | { type: 'tick'; ticker: string; price: number; receivedAt: number }
  | { type: 'connected' }
  | { type: 'reconnecting' };

export const initialStreamState: StreamState = { status: 'connecting', prices: {} };

export function streamReducer(state: StreamState, action: StreamAction): StreamState {
  switch (action.type) {
    case 'tick':
      return {
        ...state,
        prices: {
          ...state.prices,
          [action.ticker]: { price: action.price, receivedAt: action.receivedAt },
        },
      };
    case 'connected':
      return { ...state, status: 'connected' };
    case 'reconnecting':
      return { ...state, status: 'reconnecting' };
    default: {
      const exhaustive: never = action;
      return exhaustive;
    }
  }
}

export function isStale(state: StreamState, ticker: string, now: number): boolean {
  const entry = state.prices[ticker];
  if (entry === undefined) return true;

  return now - entry.receivedAt >= STALE_AFTER_MS;
}
```

The `never` binding in the `default` branch is the exhaustiveness check the spec calls for — adding a new action type without handling it becomes a compile error.

- [ ] **Step 4: Run to verify it passes**

```bash
pnpm --filter @marketpulse/dashboard test streamReducer
```

Expected: PASS, 8 tests.

- [ ] **Step 5: Write the SignalR binding**

`apps/dashboard/src/features/prices/usePriceStream.ts`:

```ts
import { tickSchema } from '@marketpulse/api-client';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { useEffect, useReducer } from 'react';
import { API_BASE_URL } from '../../api';
import { initialStreamState, streamReducer } from './streamReducer';

export function usePriceStream() {
  const [state, dispatch] = useReducer(streamReducer, initialStreamState);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/prices`)
      .withAutomaticReconnect([0, 2000, 5000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('tick', (payload: unknown) => {
      const parsed = tickSchema.safeParse(payload);
      if (!parsed.success) return;

      dispatch({
        type: 'tick',
        ticker: parsed.data.ticker,
        price: parsed.data.price,
        receivedAt: Date.now(),
      });
    });

    connection.onreconnecting(() => dispatch({ type: 'reconnecting' }));
    connection.onreconnected(() => dispatch({ type: 'connected' }));
    connection.onclose(() => dispatch({ type: 'reconnecting' }));

    void connection.start().then(() => dispatch({ type: 'connected' }));

    return () => {
      if (connection.state !== HubConnectionState.Disconnected) {
        void connection.stop();
      }
    };
  }, []);

  return state;
}
```

- [ ] **Step 6: Write the price cell**

`apps/dashboard/src/features/prices/PriceCell.tsx`:

```tsx
import { memo } from 'react';

interface PriceCellProps {
  ticker: string;
  price: number | undefined;
  stale: boolean;
  disconnected: boolean;
}

export const PriceCell = memo(function PriceCell({
  ticker,
  price,
  stale,
  disconnected,
}: PriceCellProps) {
  const dimmed = stale || disconnected;

  return (
    <span
      aria-label={`${ticker} price`}
      style={{ opacity: dimmed ? 0.4 : 1 }}
      title={disconnected ? 'Reconnecting…' : stale ? 'No recent update' : undefined}
    >
      {price === undefined ? '—' : `$${price.toFixed(2)}`}
    </span>
  );
});
```

- [ ] **Step 7: Wire the stream into the screen**

In `apps/dashboard/src/features/watchlist/WatchlistScreen.tsx`, add these imports:

```tsx
import { PriceCell } from '../prices/PriceCell';
import { isStale } from '../prices/streamReducer';
import { usePriceStream } from '../prices/usePriceStream';
```

Add inside the component, above `handleSubmit`:

```tsx
  const stream = usePriceStream();
  const now = Date.now();
```

Replace the `<li>` body with:

```tsx
          <li key={item.ticker}>
            <span>{item.ticker}</span>
            <PriceCell
              ticker={item.ticker}
              price={stream.prices[item.ticker]?.price}
              stale={isStale(stream, item.ticker, now)}
              disconnected={stream.status === 'reconnecting'}
            />
            <button
              type="button"
              onClick={() => removeItem.mutate(item.ticker)}
              aria-label={`Remove ${item.ticker}`}
            >
              Remove
            </button>
          </li>
```

Add a connection banner directly below the `<h2>`:

```tsx
      {stream.status === 'reconnecting' && (
        <p role="status">Reconnecting to the price feed…</p>
      )}
```

- [ ] **Step 8: Stub the hub in the existing screen test**

The screen now opens a SignalR connection, which MSW is configured to reject via
`onUnhandledRequest: 'error'`. In
`apps/dashboard/src/features/watchlist/WatchlistScreen.test.tsx`, add `vi` to the existing
vitest import:

```tsx
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
```

then add this immediately below the import block, above `const server = setupServer(`:

```tsx
vi.mock('../prices/usePriceStream', () => ({
  usePriceStream: () => ({ status: 'connected' as const, prices: {} }),
}));
```

This is deliberate: the hub binding is not what these tests are about, and the reducer
tests already cover the stream logic.

- [ ] **Step 9: Run the full frontend suite**

```bash
pnpm test
```

Expected: PASS, 14 tests across both packages.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: render live prices with stale and reconnecting states"
```

---

## Task 10: Dockerfile and CI

**Files:**
- Create: `src/MarketPulse.Api/Dockerfile`, `.dockerignore`, `.github/workflows/ci.yml`

**Interfaces:**
- Produces: a CI workflow that must be green before any later slice begins.

- [ ] **Step 1: Write the Dockerfile**

`src/MarketPulse.Api/Dockerfile` (build context is the repository root):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/MarketPulse.Domain/*.csproj src/MarketPulse.Domain/
COPY src/MarketPulse.Application/*.csproj src/MarketPulse.Application/
COPY src/MarketPulse.Infrastructure/*.csproj src/MarketPulse.Infrastructure/
COPY src/MarketPulse.Api/*.csproj src/MarketPulse.Api/
RUN dotnet restore src/MarketPulse.Api/MarketPulse.Api.csproj

COPY src/ src/
RUN dotnet publish src/MarketPulse.Api/MarketPulse.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "MarketPulse.Api.dll"]
```

`.dockerignore`:

```
**/bin
**/obj
**/node_modules
**/dist
.git
```

- [ ] **Step 2: Write the CI workflow**

`.github/workflows/ci.yml`:

```yaml
name: CI

on:
  pull_request:
  push:
    branches: [main]

jobs:
  backend:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-build -c Release --verbosity normal

  frontend:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: pnpm/action-setup@v4
        with:
          version: 11
      - uses: actions/setup-node@v4
        with:
          node-version: '24'
          cache: pnpm
      - run: pnpm install --frozen-lockfile
      - run: pnpm typecheck
      - run: pnpm test
      - run: pnpm --filter @marketpulse/dashboard build

  docker:
    runs-on: ubuntu-latest
    needs: [backend, frontend]
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-buildx-action@v3
      - uses: docker/build-push-action@v6
        with:
          context: .
          file: src/MarketPulse.Api/Dockerfile
          push: false
          tags: marketpulse-api:ci
```

The `backend` job runs Testcontainers against real SQL Server. GitHub's `ubuntu-latest`
runners are amd64, so no Rosetta concern applies there — that constraint is local-only.

- [ ] **Step 3: Verify the Docker build locally**

```bash
docker build -f src/MarketPulse.Api/Dockerfile -t marketpulse-api:local .
```

Expected: build succeeds. On arm64 this produces an arm64 image, which is correct for local use.

- [ ] **Step 4: Commit and push, then confirm CI is green**

```bash
git add -A
git commit -m "ci: add build, test, and docker workflow"
git push -u origin main
gh run watch
```

Expected: all three jobs pass. **Do not proceed to Task 11 with red CI.**

---

## Task 11: Documentation and the .NET 10 amendment

**Files:**
- Create: `docs/adr/001-modular-monolith-plus-one-service.md`, `docs/adr/002-walking-skeleton-first.md`, `docs/TESTING.md`
- Modify: `README.md`, `docs/MarketPulse-Pro-README.md`, `docs/superpowers/specs/2026-07-31-walking-skeleton-design.md`

- [ ] **Step 1: Write ADR-001**

`docs/adr/001-modular-monolith-plus-one-service.md`:

```markdown
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
  consumers, or eventual consistency — categories 11 in the interview map.
- **Microservices throughout.** Four or five services would each need their own pipeline,
  database, and observability wiring, for a system with one user. The cost is real and the
  benefit is imaginary at this size.

## Consequences

One RabbitMQ dependency and one extra deployable from Phase 3. The module boundary between
Portfolio and Market Data must stay clean enough that either could be extracted later —
enforced by project references, not convention.
```

- [ ] **Step 2: Write ADR-002**

`docs/adr/002-walking-skeleton-first.md`:

```markdown
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
```

- [ ] **Step 3: Write TESTING.md**

`docs/TESTING.md`:

```markdown
# Testing strategy

Trophy-shaped: heaviest at integration, thin but present at unit and end-to-end.

## What runs

| Level | Tooling | Scope |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants, `RandomWalk` bounds |
| Application unit | xUnit + NSubstitute | Handler orchestration with a substituted repository |
| Architecture | xUnit + reflection | `Domain` references nothing outside the BCL |
| Backend integration | WebApplicationFactory + Testcontainers | Real SQL Server, real migration, real HTTP |
| Frontend unit | Vitest | Stream reducer, zod schemas |
| Frontend integration | Vitest + RTL + MSW | Watchlist screen, optimistic add, 409 error path |

## Deliberately not tested in slice 1

- **No E2E suite.** Playwright arrives with real authentication in slice 2. A journey
  through a stubbed login tests the stub.
- **No coverage threshold.** A threshold over a codebase this small drives noise, not
  quality. It arrives when there is a codebase to threshold.
- **No SignalR transport unit test.** That would test Microsoft's library. The one
  integration test in `PriceStreamTests` proves our chain end to end; the reducer tests
  cover our logic.
```

- [ ] **Step 4: Amend the .NET version references**

The project targets `net10.0`, not .NET 8. Update:

- `docs/MarketPulse-Pro-README.md` line 7: change the badge to `.NET-10.0-512BD4`
- `docs/MarketPulse-Pro-README.md` line 122: change `.NET 8` to `.NET 10`
- `docs/MarketPulse-Pro-README.md` line 356: change `.NET 8 SDK` to `.NET 10 SDK`
- `docs/MarketPulse-Pro-README.md` lines 52, 69: change `.NET 8` / `ASP.NET Core 8` to 10
- `docs/superpowers/specs/2026-07-31-walking-skeleton-design.md`: add a line under
  "Decisions taken" recording that .NET 10 replaced .NET 8, because .NET 8 reaches
  end-of-life in November 2026 and only the .NET 10 SDK was installed.

- [ ] **Step 5: Promote the root README**

```bash
cp docs/MarketPulse-Pro-README.md README.md
```

Then fix the now-relative doc links inside `README.md`: paths written as
`docs/adr/...` remain correct from the repository root, so only verify — do not rewrite.

- [ ] **Step 6: Run everything one final time**

```bash
docker compose up -d
dotnet test
pnpm test
```

Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "docs: add ADR-001, ADR-002, testing strategy, and .NET 10 amendment"
```

---

## Definition of done

Verified against the spec's done criteria:

- [ ] `docker compose up -d` → `dotnet run --project src/MarketPulse.Api` → `pnpm --filter @marketpulse/dashboard dev` yields a working watchlist
- [ ] Adding IVV persists across an API restart
- [ ] Every ticker on the watchlist updates once per second
- [ ] Killing the API greys the cells; restarting reconnects without a page refresh
- [ ] `dotnet test` passes with Testcontainers starting real SQL Server
- [ ] `pnpm test` passes
- [ ] CI green on a PR, Docker image built
- [ ] ADR-001 and ADR-002 committed, each with rejected alternatives
- [ ] `TESTING.md` records the three deliberate gaps
- [ ] Root `README.md` promoted from the 14-byte stub
