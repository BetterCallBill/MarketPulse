# Slice 5a — Portfolio Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The `Portfolio` domain — buy/sell transactions, average-cost basis, realised P&L — behind a user-scoped API with a stored-key idempotency mechanism (also retrofitted onto `POST /alerts`), proven by TDD unit tests, an oversell anomaly pair, and integration tests; plus ADR-004.

**Architecture:** `Portfolio` aggregate holds per-ticker `Holding`s (owned entities, bounded) with `RowVersion` optimistic concurrency; `Transaction` is a regular append-only entity minted only by the aggregate (internal constructor) so the write path stays behind invariants while the read path pages the table directly — which is exactly the CQRS-lite scope ADR-004 documents. Idempotency is an action filter over a claimed-first stored-key table. No messaging changes.

**Tech Stack:** .NET 10, EF Core (SQL Server, migration), MediatR + FluentValidation (existing pipeline), xUnit + Testcontainers.

## Global Constraints

- Branch: `feature/slice-5a-portfolio-backend` (exists; spec committed). Merges into `test`.
- Conventional commits, **no Co-Authored-By trailer**.
- Spec: `docs/superpowers/specs/2026-08-05-portfolio-backend-design.md`. Out of scope there is out of scope here: no portfolio UI, no domain events, no FIFO, no fees/dividends/cash, no transaction delete/amend, no idempotency beyond the two named endpoints, no key expiry.
- TDD on the aggregate and everywhere a failing test is meaningful; RED/GREEN evidence required.
- `DependencyRuleTests` stays green: Domain references nothing; Application references Domain only.
- Precision: units `decimal(18,6)`; prices/costs/P&L `decimal(18,4)`. Money/units are `decimal` end to end.
- Error codes/status through `DomainException` subclasses (`ErrorCode` slug + `StatusCode`), surfaced by the existing `ExceptionHandlingMiddleware`.
- Run unit tests: `dotnet test tests/MarketPulse.UnitTests --filter <name>`; integration: `dotnet test tests/MarketPulse.IntegrationTests --filter <name>` (Docker required, compose infra up).
- Migrations: `dotnet ef migrations add <Name> --project src/MarketPulse.Infrastructure --startup-project src/MarketPulse.Api` (a `DesignTimeDbContextFactory` exists). Integration tests apply migrations via `SqlServerFixture`; the dev database gets them via `dotnet ef database update` (same projects) — run it once after the migration task so local `dotnet run` keeps working.
- API wire casing camelCase; `side` travels as a string (`"Buy"`/`"Sell"`), validated like alert `direction`.

---

### Task 1: The Portfolio aggregate (TDD)

**Files:**
- Create: `src/MarketPulse.Domain/Entities/Portfolio.cs`
- Create: `src/MarketPulse.Domain/Entities/Holding.cs`
- Create: `src/MarketPulse.Domain/Entities/Transaction.cs`
- Create: `src/MarketPulse.Domain/Exceptions/PortfolioExceptions.cs`
- Test: `tests/MarketPulse.UnitTests/Domain/PortfolioTests.cs`

**Interfaces:**
- Produces (later tasks rely on these exact members):
  - `Portfolio` — `Guid Id`, `Guid UserId`, `byte[] RowVersion`, `IReadOnlyCollection<Holding> Holdings`; `static Portfolio Create(Guid userId)`; `Transaction RecordBuy(string ticker, decimal units, decimal price, DateTimeOffset occurredUtc, DateTimeOffset recordedUtc)`; `Transaction RecordSell(...)` (same signature).
  - `Holding` — `Guid Id`, `Guid PortfolioId`, `string Ticker`, `decimal Units`, `decimal AverageCost`, `decimal RealisedPnL` (no public mutators).
  - `Transaction` — `Guid Id`, `Guid PortfolioId`, `string Ticker`, `TransactionSide Side`, `decimal Units`, `decimal Price`, `DateTimeOffset OccurredUtc`, `DateTimeOffset RecordedUtc`; **internal** constructor.
  - `enum TransactionSide { Buy, Sell }`
  - Exceptions: `InsufficientHoldingsException(decimal held, decimal requested)` (422, `insufficient-holdings`), `InvalidTradeException(string reason)` (400, `invalid-trade`).

- [ ] **Step 1: Write the failing tests**

`PortfolioTests.cs` — house style is one behaviour per `[Fact]` with sentence names (see `WatchlistTests.cs`):

```csharp
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.UnitTests.Domain;

public class PortfolioTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    private static Portfolio NewPortfolio() => Portfolio.Create(Guid.NewGuid());

    [Fact]
    public void A_first_buy_creates_the_holding_at_the_fill_price()
    {
        var portfolio = NewPortfolio();

        var tx = portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal("IVV", holding.Ticker);
        Assert.Equal(10m, holding.Units);
        Assert.Equal(60m, holding.AverageCost);
        Assert.Equal(0m, holding.RealisedPnL);
        Assert.Equal(TransactionSide.Buy, tx.Side);
        Assert.Equal(portfolio.Id, tx.PortfolioId);
    }

    [Fact]
    public void A_second_buy_reaverages_the_cost()
    {
        // 10 @ 60 then 10 @ 80 → 20 units at (600 + 800) / 20 = 70.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);

        portfolio.RecordBuy("IVV", 10m, 80m, Now, Now);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(20m, holding.Units);
        Assert.Equal(70m, holding.AverageCost);
    }

    [Fact]
    public void A_sell_realises_pnl_against_average_cost_and_leaves_the_average_unchanged()
    {
        // 20 @ avg 70, sell 5 @ 90 → realised 5 · (90 − 70) = 100; avg stays 70.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);
        portfolio.RecordBuy("IVV", 10m, 80m, Now, Now);

        portfolio.RecordSell("IVV", 5m, 90m, Now, Now);

        var holding = Assert.Single(portfolio.Holdings);
        Assert.Equal(15m, holding.Units);
        Assert.Equal(70m, holding.AverageCost);
        Assert.Equal(100m, holding.RealisedPnL);
    }

    [Fact]
    public void Selling_at_a_loss_realises_negative_pnl()
    {
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("NDQ", 10m, 50m, Now, Now);

        portfolio.RecordSell("NDQ", 4m, 45m, Now, Now);

        Assert.Equal(-20m, Assert.Single(portfolio.Holdings).RealisedPnL);
    }

    [Fact]
    public void Selling_to_zero_keeps_the_holding_and_a_rebuy_resets_the_basis()
    {
        // The basis reset falls out of the averaging arithmetic, not a special case:
        // (0 · anything + 10 · 40) / 10 = 40.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);
        portfolio.RecordSell("IVV", 10m, 65m, Now, Now);

        var flat = Assert.Single(portfolio.Holdings);
        Assert.Equal(0m, flat.Units);
        Assert.Equal(50m, flat.RealisedPnL); // 10 · (65 − 60)

        portfolio.RecordBuy("IVV", 10m, 40m, Now, Now);

        var rebought = Assert.Single(portfolio.Holdings);
        Assert.Equal(10m, rebought.Units);
        Assert.Equal(40m, rebought.AverageCost);
        Assert.Equal(50m, rebought.RealisedPnL); // history survives the flat period
    }

    [Fact]
    public void Overselling_throws_and_names_both_quantities()
    {
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("IVV", 5m, 60m, Now, Now);

        var ex = Assert.Throws<InsufficientHoldingsException>(
            () => portfolio.RecordSell("IVV", 6m, 60m, Now, Now));

        Assert.Contains("5", ex.Message);
        Assert.Contains("6", ex.Message);
        Assert.Equal(422, ex.StatusCode);
    }

    [Fact]
    public void Selling_a_ticker_never_bought_throws()
    {
        Assert.Throws<InsufficientHoldingsException>(
            () => NewPortfolio().RecordSell("IVV", 1m, 60m, Now, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_units_are_rejected_on_both_sides(decimal units)
    {
        var portfolio = NewPortfolio();
        Assert.Throws<InvalidTradeException>(() => portfolio.RecordBuy("IVV", units, 60m, Now, Now));
        Assert.Throws<InvalidTradeException>(() => portfolio.RecordSell("IVV", units, 60m, Now, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void Non_positive_prices_are_rejected(decimal price)
    {
        Assert.Throws<InvalidTradeException>(
            () => NewPortfolio().RecordBuy("IVV", 1m, price, Now, Now));
    }

    [Fact]
    public void Fractional_units_average_exactly_in_decimal()
    {
        // 0.3 @ 100 and 0.6 @ 40 → 0.9 units at (30 + 24) / 0.9 = 60 exactly — the
        // arithmetic that would drift in double stays exact in decimal.
        var portfolio = NewPortfolio();
        portfolio.RecordBuy("VHY", 0.3m, 100m, Now, Now);
        portfolio.RecordBuy("VHY", 0.6m, 40m, Now, Now);

        Assert.Equal(60m, Assert.Single(portfolio.Holdings).AverageCost);
    }

    [Fact]
    public void Tickers_are_normalised_like_the_rest_of_the_domain()
    {
        var portfolio = NewPortfolio();
        portfolio.RecordBuy(" ivv ", 1m, 60m, Now, Now);

        Assert.Equal("IVV", Assert.Single(portfolio.Holdings).Ticker);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MarketPulse.UnitTests --filter PortfolioTests`
Expected: FAIL — types don't exist (compile error is the RED here; note it as such).

- [ ] **Step 3: Implement the domain types**

`PortfolioExceptions.cs`:

```csharp
namespace MarketPulse.Domain.Exceptions;

public sealed class InsufficientHoldingsException(decimal held, decimal requested)
    : DomainException(
        $"Cannot sell {requested} units: only {held} held.")
{
    public override string ErrorCode => "insufficient-holdings";
    public override int StatusCode => 422;
}

public sealed class InvalidTradeException(string reason)
    : DomainException(reason)
{
    public override string ErrorCode => "invalid-trade";
    public override int StatusCode => 400;
}
```

`Transaction.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

public enum TransactionSide { Buy, Sell }

/// <summary>
/// One recorded trade. Append-only by construction: the constructor is internal so only
/// the aggregate mints one, no mutators exist, and the API exposes no delete — an
/// incorrect trade is corrected by an offsetting one (see ADR-004's consequences).
/// </summary>
public sealed class Transaction
{
    public Guid Id { get; private set; }
    public Guid PortfolioId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public TransactionSide Side { get; private set; }
    public decimal Units { get; private set; }
    public decimal Price { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public DateTimeOffset RecordedUtc { get; private set; }

    private Transaction() { }

    internal Transaction(
        Guid portfolioId, string ticker, TransactionSide side,
        decimal units, decimal price, DateTimeOffset occurredUtc, DateTimeOffset recordedUtc)
    {
        Id = Guid.NewGuid();
        PortfolioId = portfolioId;
        Ticker = ticker;
        Side = side;
        Units = units;
        Price = price;
        OccurredUtc = occurredUtc;
        RecordedUtc = recordedUtc;
    }
}
```

`Holding.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

/// <summary>
/// One ticker's position inside a portfolio. Mutated only by the aggregate — the internal
/// methods are the whole write surface, and the arithmetic lives here so the invariant
/// ("average cost and realised P&L are a pure function of the transaction stream") has one
/// home.
/// </summary>
public sealed class Holding
{
    public Guid Id { get; private set; }
    public Guid PortfolioId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public decimal Units { get; private set; }
    public decimal AverageCost { get; private set; }
    public decimal RealisedPnL { get; private set; }

    private Holding() { }

    internal Holding(Guid portfolioId, string ticker)
    {
        Id = Guid.NewGuid();
        PortfolioId = portfolioId;
        Ticker = ticker;
    }

    internal void ApplyBuy(decimal units, decimal price)
    {
        AverageCost = (Units * AverageCost + units * price) / (Units + units);
        Units += units;
    }

    internal void ApplySell(decimal units, decimal price)
    {
        RealisedPnL += units * (price - AverageCost);
        Units -= units;
        // AverageCost deliberately unchanged: a sell realises against it, never moves it.
        // A later buy from zero re-averages from (0 · avg) — the basis reset for free.
    }
}
```

`Portfolio.cs`:

```csharp
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Domain.Entities;

/// <summary>
/// One per user, created implicitly on the first transaction. Holdings are bounded (one per
/// ticker) and live inside the aggregate; transactions are unbounded, so the aggregate mints
/// them (internal constructor — nothing else can) but does not hold them: the repository
/// persists what RecordBuy/RecordSell return, and reads page the table directly. That split
/// is the CQRS scope ADR-004 documents.
/// </summary>
public sealed class Portfolio
{
    private readonly List<Holding> _holdings = [];

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public IReadOnlyCollection<Holding> Holdings => _holdings.AsReadOnly();

    /// <summary>
    /// Two concurrent sells of the same holding must not oversell. The loser's UPDATE
    /// matches no row; the API answers 409 and the client retries against fresh state.
    /// The anomaly test pair demonstrates exactly this with the token bypassed and not.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    private Portfolio() { }

    public static Portfolio Create(Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId
    };

    public Transaction RecordBuy(
        string ticker, decimal units, decimal price,
        DateTimeOffset occurredUtc, DateTimeOffset recordedUtc)
    {
        var code = Validate(ticker, units, price);

        var holding = _holdings.FirstOrDefault(h => h.Ticker == code);
        if (holding is null)
        {
            holding = new Holding(Id, code);
            _holdings.Add(holding);
        }

        holding.ApplyBuy(units, price);
        return new Transaction(Id, code, TransactionSide.Buy, units, price, occurredUtc, recordedUtc);
    }

    public Transaction RecordSell(
        string ticker, decimal units, decimal price,
        DateTimeOffset occurredUtc, DateTimeOffset recordedUtc)
    {
        var code = Validate(ticker, units, price);

        var holding = _holdings.FirstOrDefault(h => h.Ticker == code);
        var held = holding?.Units ?? 0m;
        if (holding is null || held < units)
        {
            throw new InsufficientHoldingsException(held, units);
        }

        holding.ApplySell(units, price);
        return new Transaction(Id, code, TransactionSide.Sell, units, price, occurredUtc, recordedUtc);
    }

    private static string Validate(string ticker, decimal units, decimal price)
    {
        if (units <= 0)
        {
            throw new InvalidTradeException("Units must be greater than zero.");
        }

        if (price <= 0)
        {
            throw new InvalidTradeException("Price must be greater than zero.");
        }

        return ticker.Trim().ToUpperInvariant();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter PortfolioTests`
Expected: PASS (12 tests). Then the whole unit project once: `dotnet test tests/MarketPulse.UnitTests` — `DependencyRuleTests` must still pass.

- [ ] **Step 5: Commit**

```bash
git add src/MarketPulse.Domain tests/MarketPulse.UnitTests/Domain/PortfolioTests.cs
git commit -m "feat(domain): Portfolio aggregate with average-cost basis and realised P&L"
```

---

### Task 2: Persistence — mapping, migration, repository

**Files:**
- Modify: `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`
- Create: `src/MarketPulse.Application/Abstractions/IPortfolioRepository.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/PortfolioRepository.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (register in `AddPersistence`)
- Create: migration `Slice5aPortfolio` (generated)
- Test: `tests/MarketPulse.IntegrationTests/PortfolioPersistenceTests.cs`

**Interfaces:**
- Produces:
  - `IPortfolioRepository`: `Task<Portfolio?> GetForUserAsync(Guid userId, CancellationToken ct)` (holdings included), `Task AddAsync(Portfolio portfolio, CancellationToken ct)`, `Task AddTransactionAsync(Transaction transaction, CancellationToken ct)`, `Task<IReadOnlyList<Transaction>> GetTransactionsAsync(Guid portfolioId, int skip, int take, CancellationToken ct)` (newest `OccurredUtc` first, then `RecordedUtc`), `Task<bool> TickerExistsAsync(string code, CancellationToken ct)`, `Task SaveChangesAsync(CancellationToken ct)`.
  - DbSets: `Portfolios`, `Transactions` on `MarketPulseDbContext` (Holdings mapped as owned — reachable via the aggregate, and via `Set<Holding>()` nowhere: tests read holdings through the aggregate).

- [ ] **Step 1: Write the failing persistence test**

```csharp
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PortfolioPersistenceTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_portfolio_round_trips_with_holdings_transactions_and_precision_intact()
    {
        var userId = await NewUserAsync();
        var portfolio = Portfolio.Create(userId);
        var tx1 = portfolio.RecordBuy("IVV", 0.123456m, 60.1234m, Now, Now);
        var tx2 = portfolio.RecordBuy("IVV", 0.2m, 61m, Now, Now);

        await using (var db = fixture.CreateContext())
        {
            db.Portfolios.Add(portfolio);
            db.Transactions.AddRange(tx1, tx2);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var loaded = await db.Portfolios
                .SingleAsync(p => p.UserId == userId);
            var holding = Assert.Single(loaded.Holdings);

            // Units survive at (18,6); money at (18,4).
            Assert.Equal(0.323456m, holding.Units);
            Assert.NotEmpty(loaded.RowVersion);

            Assert.Equal(2, await db.Transactions.CountAsync(t => t.PortfolioId == loaded.Id));
        }
    }

    [Fact]
    public async Task A_stale_portfolio_write_throws_the_concurrency_exception()
    {
        var userId = await NewUserAsync();
        var portfolio = Portfolio.Create(userId);
        portfolio.RecordBuy("IVV", 10m, 60m, Now, Now);

        await using (var db = fixture.CreateContext())
        {
            db.Portfolios.Add(portfolio);
            await db.SaveChangesAsync();
        }

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var copy1 = await first.Portfolios.SingleAsync(p => p.UserId == userId);
        var copy2 = await second.Portfolios.SingleAsync(p => p.UserId == userId);

        copy1.RecordSell("IVV", 1m, 60m, Now, Now);
        await first.SaveChangesAsync();

        copy2.RecordSell("IVV", 1m, 60m, Now, Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    private async Task<Guid> NewUserAsync()
    {
        // A real user row, because Portfolios FK onto Users. Register over HTTP like the
        // other persistence suites do (see WatchlistPersistenceTests for the pattern) or
        // insert directly — match whichever that file actually uses.
        await using var factory = TestFactory.Create(fixture);
        var email = AuthenticatedClient.NewEmail();
        _ = await AuthenticatedClient.RegisterAsync(factory, email);

        await using var db = fixture.CreateContext();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }
}
```

(Adjust `NewUserAsync` to mirror how `WatchlistPersistenceTests`/`AlertPersistenceTests` actually create users — read one of them first and copy its approach exactly. Note whether holdings load without an explicit `Include`: owned collections load with the owner by default, which is why the test asserts through the aggregate.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter PortfolioPersistenceTests`
Expected: FAIL — no `Portfolios` DbSet.

- [ ] **Step 3: Map, migrate, implement**

`MarketPulseDbContext` — add DbSets:

```csharp
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
```

and in `OnModelCreating`, following the `Watchlist` owned-collection commentary (the `Ignore` + field-mapped `OwnsMany` + `ValueGeneratedNever` reasoning is already written there — reference it rather than repeating the comment block):

```csharp
        b.Entity<Portfolio>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId).IsUnique();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Same shape as Watchlist: computed property ignored, field-mapped owned set.
            e.Ignore(x => x.Holdings);

            e.OwnsMany<Holding>("_holdings", holdings =>
            {
                holdings.ToTable("Holdings");
                holdings.WithOwner().HasForeignKey(x => x.PortfolioId);
                holdings.HasKey(x => x.Id);
                holdings.Property(x => x.Id).ValueGeneratedNever();
                holdings.Property(x => x.Ticker).HasMaxLength(8).IsRequired();
                holdings.Property(x => x.Units).HasPrecision(18, 6);
                holdings.Property(x => x.AverageCost).HasPrecision(18, 4);
                holdings.Property(x => x.RealisedPnL).HasPrecision(18, 4);
                holdings.HasIndex(x => new { x.PortfolioId, x.Ticker }).IsUnique();
            });
        });

        b.Entity<Transaction>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Ticker).HasMaxLength(8).IsRequired();
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(8).IsRequired();
            e.Property(x => x.Units).HasPrecision(18, 6);
            e.Property(x => x.Price).HasPrecision(18, 4);

            // The history query: one portfolio's trades, newest first.
            e.HasIndex(x => new { x.PortfolioId, x.OccurredUtc });

            e.HasOne<Portfolio>().WithMany().HasForeignKey(x => x.PortfolioId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

`IPortfolioRepository.cs` (Application/Abstractions — mirror `IAlertRuleRepository`'s doc-comment style):

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IPortfolioRepository
{
    /// <summary>Holdings load with the aggregate (owned collection). Null when the user
    /// has never traded — creation is the caller's job, on first transaction.</summary>
    Task<Portfolio?> GetForUserAsync(Guid userId, CancellationToken ct);

    Task AddAsync(Portfolio portfolio, CancellationToken ct);

    /// <summary>Persisted beside the aggregate change in the same unit of work: the trade
    /// and its effect on the holding commit together or not at all.</summary>
    Task AddTransactionAsync(Transaction transaction, CancellationToken ct);

    /// <summary>Newest first. Reads page the table directly — the aggregate does not hold
    /// its unbounded history (ADR-004).</summary>
    Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        Guid portfolioId, int skip, int take, CancellationToken ct);

    Task<bool> TickerExistsAsync(string code, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
```

`PortfolioRepository.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class PortfolioRepository(MarketPulseDbContext db) : IPortfolioRepository
{
    public Task<Portfolio?> GetForUserAsync(Guid userId, CancellationToken ct) =>
        db.Portfolios.FirstOrDefaultAsync(p => p.UserId == userId, ct);

    public async Task AddAsync(Portfolio portfolio, CancellationToken ct) =>
        await db.Portfolios.AddAsync(portfolio, ct);

    public async Task AddTransactionAsync(Transaction transaction, CancellationToken ct) =>
        await db.Transactions.AddAsync(transaction, ct);

    public async Task<IReadOnlyList<Transaction>> GetTransactionsAsync(
        Guid portfolioId, int skip, int take, CancellationToken ct) =>
        await db.Transactions
            .Where(t => t.PortfolioId == portfolioId)
            .OrderByDescending(t => t.OccurredUtc)
            .ThenByDescending(t => t.RecordedUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    public Task<bool> TickerExistsAsync(string code, CancellationToken ct) =>
        db.Tickers.AnyAsync(t => t.Code == code, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
```

Register in `AddPersistence` beside the other repositories:

```csharp
        services.AddScoped<IPortfolioRepository, PortfolioRepository>();
```

Generate the migration and update the dev database:

```bash
dotnet ef migrations add Slice5aPortfolio --project src/MarketPulse.Infrastructure --startup-project src/MarketPulse.Api
dotnet ef database update --project src/MarketPulse.Infrastructure --startup-project src/MarketPulse.Api
```

Inspect the generated migration: tables `Portfolios`, `Holdings`, `Transactions`; rowversion column; the two unique indexes; precisions as specified. If EF generated anything unexpected (extra shadow columns, wrong precision), fix the mapping, remove and re-add the migration.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter PortfolioPersistenceTests`
Expected: PASS. Then `dotnet test tests/MarketPulse.UnitTests` (dependency rules still green).

- [ ] **Step 5: Commit**

```bash
git add src/MarketPulse.Domain src/MarketPulse.Application src/MarketPulse.Infrastructure tests/MarketPulse.IntegrationTests/PortfolioPersistenceTests.cs
git commit -m "feat(persistence): map and migrate Portfolio, Holdings, Transactions"
```

---

### Task 3: Application layer — record, portfolio, history

**Files:**
- Create: `src/MarketPulse.Application/Portfolios/RecordTransactionCommand.cs`
- Create: `src/MarketPulse.Application/Portfolios/GetPortfolioQuery.cs`
- Create: `src/MarketPulse.Application/Portfolios/GetTransactionsQuery.cs`

**Interfaces:**
- Consumes: `IPortfolioRepository`, `ICurrentUser` (existing — exposes `UserId`), the domain from Task 1.
- Produces (Task 4's controller and tests bind to these):
  - `record PortfolioDto(IReadOnlyList<HoldingDto> Holdings, decimal TotalRealisedPnL)`
  - `record HoldingDto(string Ticker, decimal Units, decimal AverageCost, decimal RealisedPnL)`
  - `record TransactionDto(Guid Id, string Ticker, string Side, decimal Units, decimal Price, DateTimeOffset OccurredUtc, DateTimeOffset RecordedUtc)`
  - `record RecordTransactionCommand(string Ticker, string Side, decimal Units, decimal Price, DateTimeOffset? OccurredUtc) : IRequest<PortfolioDto>`
  - `record GetPortfolioQuery : IRequest<PortfolioDto>`
  - `record GetTransactionsQuery(int Skip = 0, int Take = 50) : IRequest<IReadOnlyList<TransactionDto>>`

- [ ] **Step 1: Implement command, validator, handler**

`RecordTransactionCommand.cs` (one file per feature folder, the alerts pattern — command + validator + handler together):

```csharp
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Portfolios;

public record HoldingDto(string Ticker, decimal Units, decimal AverageCost, decimal RealisedPnL);

public record PortfolioDto(IReadOnlyList<HoldingDto> Holdings, decimal TotalRealisedPnL);

/// <summary>
/// Side arrives as a string for the same reason alert Direction does: a bad value should
/// be a readable 400, not a model-binding failure. OccurredUtc is optional — a trade
/// recorded after the fact keeps its real timestamp; an omitted one is "now".
/// </summary>
public record RecordTransactionCommand(
    string Ticker, string Side, decimal Units, decimal Price, DateTimeOffset? OccurredUtc)
    : IRequest<PortfolioDto>;

public sealed class RecordTransactionValidator : AbstractValidator<RecordTransactionCommand>
{
    public RecordTransactionValidator(IPortfolioRepository repo)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithMessage("Ticker is required.").WithErrorCode("invalid-ticker")
            .MaximumLength(8).WithMessage("Ticker must be 8 characters or fewer.")
                .WithErrorCode("invalid-ticker")
            .MustAsync(async (ticker, ct) =>
                await repo.TickerExistsAsync(ticker.Trim().ToUpperInvariant(), ct))
            .WithMessage(x => $"'{x.Ticker}' is not a known ticker.")
            .WithErrorCode("unknown-ticker");

        RuleFor(x => x.Side)
            .Must(s => Enum.TryParse<TransactionSide>(s, ignoreCase: true, out _))
            .WithMessage("Side must be 'Buy' or 'Sell'.")
            .WithErrorCode("invalid-side");

        RuleFor(x => x.Units)
            .GreaterThan(0).WithMessage("Units must be greater than zero.")
            .WithErrorCode("invalid-units");

        RuleFor(x => x.Price)
            .GreaterThan(0).WithMessage("Price must be greater than zero.")
            .WithErrorCode("invalid-price");
    }
}

public sealed class RecordTransactionHandler(IPortfolioRepository repo, ICurrentUser user)
    : IRequestHandler<RecordTransactionCommand, PortfolioDto>
{
    public async Task<PortfolioDto> Handle(RecordTransactionCommand request, CancellationToken ct)
    {
        var side = Enum.Parse<TransactionSide>(request.Side, ignoreCase: true);
        var occurred = request.OccurredUtc ?? DateTimeOffset.UtcNow;
        var recorded = DateTimeOffset.UtcNow;

        var portfolio = await repo.GetForUserAsync(user.UserId, ct);
        if (portfolio is null)
        {
            // Implicit creation: "no portfolio yet" is not a state the API exposes.
            portfolio = Portfolio.Create(user.UserId);
            await repo.AddAsync(portfolio, ct);
        }

        var transaction = side == TransactionSide.Buy
            ? portfolio.RecordBuy(request.Ticker, request.Units, request.Price, occurred, recorded)
            : portfolio.RecordSell(request.Ticker, request.Units, request.Price, occurred, recorded);

        // One unit of work: the holding's new state and the trade that caused it commit
        // together — the spec's "cannot lose a transaction" is this line pair.
        await repo.AddTransactionAsync(transaction, ct);
        await repo.SaveChangesAsync(ct);

        return PortfolioMapper.ToDto(portfolio);
    }
}

internal static class PortfolioMapper
{
    public static PortfolioDto ToDto(Domain.Entities.Portfolio? portfolio)
    {
        var holdings = portfolio?.Holdings
            .OrderBy(h => h.Ticker)
            .Select(h => new HoldingDto(h.Ticker, h.Units, h.AverageCost, h.RealisedPnL))
            .ToList() ?? [];

        return new PortfolioDto(holdings, holdings.Sum(h => h.RealisedPnL));
    }
}
```

`GetPortfolioQuery.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Portfolios;

public record GetPortfolioQuery : IRequest<PortfolioDto>;

public sealed class GetPortfolioHandler(IPortfolioRepository repo, ICurrentUser user)
    : IRequestHandler<GetPortfolioQuery, PortfolioDto>
{
    public async Task<PortfolioDto> Handle(GetPortfolioQuery request, CancellationToken ct) =>
        // A user who never traded gets an empty portfolio, not a 404 — implicit creation
        // means absence is indistinguishable from emptiness, on purpose.
        PortfolioMapper.ToDto(await repo.GetForUserAsync(user.UserId, ct));
}
```

`GetTransactionsQuery.cs` (clamping mirrors `GetNotificationsQuery` — read that file and copy its take/skip clamping exactly):

```csharp
using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Portfolios;

public record TransactionDto(
    Guid Id, string Ticker, string Side, decimal Units, decimal Price,
    DateTimeOffset OccurredUtc, DateTimeOffset RecordedUtc);

public record GetTransactionsQuery(int Skip = 0, int Take = 50)
    : IRequest<IReadOnlyList<TransactionDto>>;

public sealed class GetTransactionsHandler(IPortfolioRepository repo, ICurrentUser user)
    : IRequestHandler<GetTransactionsQuery, IReadOnlyList<TransactionDto>>
{
    public async Task<IReadOnlyList<TransactionDto>> Handle(
        GetTransactionsQuery request, CancellationToken ct)
    {
        var portfolio = await repo.GetForUserAsync(user.UserId, ct);
        if (portfolio is null)
        {
            return [];
        }

        var take = Math.Clamp(request.Take, 1, 100);
        var skip = Math.Max(request.Skip, 0);

        var transactions = await repo.GetTransactionsAsync(portfolio.Id, skip, take, ct);

        return transactions
            .Select(t => new TransactionDto(
                t.Id, t.Ticker, t.Side.ToString(), t.Units, t.Price, t.OccurredUtc, t.RecordedUtc))
            .ToList();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build MarketPulse.sln`
Expected: clean. (Behaviour is exercised by Task 4's API tests — the handlers are thin over the domain already covered by Task 1; do not write mock-heavy handler unit tests.)

- [ ] **Step 3: Commit**

```bash
git add src/MarketPulse.Application/Portfolios
git commit -m "feat(application): record-transaction command and portfolio queries"
```

---

### Task 4: API — controller, 409 concurrency mapping, integration tests

**Files:**
- Create: `src/MarketPulse.Api/Controllers/PortfolioController.cs`
- Modify: `src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs` (map `DbUpdateConcurrencyException` → 409)
- Test: `tests/MarketPulse.IntegrationTests/PortfolioApiTests.cs`
- Modify: `tests/MarketPulse.IntegrationTests/CrossUserIsolationTests.cs` (extend to portfolio + transactions)

**Interfaces:**
- Consumes: Task 3's commands/queries and DTOs; `AuthenticatedClient`, `TestFactory`, `SqlServerCollection` (existing test scaffolding).
- Produces: `POST /api/v1/portfolio/transactions` (201, body `PortfolioDto`), `GET /api/v1/portfolio` (200 `PortfolioDto`), `GET /api/v1/portfolio/transactions?take=&skip=` (200 `TransactionDto[]`).

- [ ] **Step 1: Write the failing API tests**

`PortfolioApiTests.cs` — follow `AlertsApiTests`/`WatchlistApiTests` conventions (`[Collection(nameof(SqlServerCollection))]`, factory per class via `TestFactory.Create(fixture)`, `AuthenticatedClient.RegisterAsync`). Response records local to the test file, the house pattern:

```csharp
private sealed record HoldingResponse(string Ticker, decimal Units, decimal AverageCost, decimal RealisedPnL);
private sealed record PortfolioResponse(List<HoldingResponse> Holdings, decimal TotalRealisedPnL);
private sealed record TransactionResponse(Guid Id, string Ticker, string Side, decimal Units, decimal Price);
```

Tests (write each as a `[Fact]` with the house sentence names):

1. `A_buy_then_partial_sell_produce_correct_average_cost_and_realised_pnl` — POST buy 10 IVV @ 60 (assert 201), POST buy 10 @ 80, POST sell 5 @ 90; GET `/api/v1/portfolio` → single holding: units 15, averageCost 70, realisedPnL 100, totalRealisedPnL 100. (This is spec done-criterion 2, end to end.)
2. `An_empty_portfolio_is_200_with_no_holdings` — fresh user, GET → 200, empty holdings, total 0.
3. `Overselling_is_422_with_the_named_code` — buy 5, sell 6 → 422, ProblemDetails title `insufficient-holdings`.
4. `An_unknown_ticker_is_400` — POST buy ZZZZ → 400, title `unknown-ticker`.
5. `A_bad_side_is_400` — POST side `"Hold"` → 400, title `invalid-side`.
6. `Transactions_page_newest_first` — three trades with distinct `occurredUtc` values passed explicitly; GET `/api/v1/portfolio/transactions?take=2` → the two newest, in order; `?skip=2` → the oldest.
7. `Transactions_for_a_fresh_user_are_empty` — 200, `[]`.

In `CrossUserIsolationTests.cs`, read the file's existing per-resource pattern and extend it: Alice records a trade; Bob's GET `/api/v1/portfolio` shows no holdings and his `GET /api/v1/portfolio/transactions` is empty. Follow exactly the structure the file already uses for alerts/notifications.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter PortfolioApiTests`
Expected: FAIL — 404s (no controller).

- [ ] **Step 3: Implement the controller and the 409 mapping**

`PortfolioController.cs` (mirror `AlertsController`'s shape — `[ApiController]`, `[Authorize]`, `[Route]`, MediatR via constructor injection; read it first and copy its attribute set exactly):

```csharp
using MarketPulse.Application.Portfolios;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/portfolio")]
public sealed class PortfolioController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PortfolioDto>> Get(CancellationToken ct) =>
        Ok(await mediator.Send(new GetPortfolioQuery(), ct));

    [HttpPost("transactions")]
    public async Task<ActionResult<PortfolioDto>> Record(
        RecordTransactionCommand command, CancellationToken ct)
    {
        var portfolio = await mediator.Send(command, ct);

        // 201 pointing at the portfolio the trade changed — the transaction itself has no
        // GET-by-id endpoint, and inventing one for a Location header repeats 4a's mistake.
        return CreatedAtAction(nameof(Get), portfolio);
    }

    [HttpGet("transactions")]
    public async Task<ActionResult<IReadOnlyList<TransactionDto>>> Transactions(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await mediator.Send(new GetTransactionsQuery(skip, take), ct));
}
```

`ExceptionHandlingMiddleware` — add a catch between `DomainException` and `UnauthorizedAccessException`:

```csharp
        catch (DbUpdateConcurrencyException)
        {
            // Two writes raced the same portfolio (or rule) and this one lost. The client
            // re-reads and retries; its sell may now legitimately fail validation instead.
            await WriteAsync(context, StatusCodes.Status409Conflict,
                "concurrent-update", "The resource was modified concurrently. Retry.");
        }
```

with `using Microsoft.EntityFrameworkCore;` added to the file's usings.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter "PortfolioApiTests|CrossUserIsolation"`
Expected: PASS, including the pre-existing isolation tests.

- [ ] **Step 5: Commit**

```bash
git add src/MarketPulse.Api tests/MarketPulse.IntegrationTests
git commit -m "feat(api): portfolio endpoints with concurrency-conflict mapping"
```

---

### Task 5: Idempotency — store, filter, both endpoints

**Files:**
- Create: `src/MarketPulse.Domain/Entities/IdempotencyKey.cs`
- Create: `src/MarketPulse.Application/Abstractions/IIdempotencyStore.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/IdempotencyStore.cs`
- Modify: `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs` (+ mapping)
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (register store, scoped)
- Create: migration `Slice5aIdempotencyKeys`
- Create: `src/MarketPulse.Api/Filters/IdempotencyFilter.cs`
- Modify: `src/MarketPulse.Api/Controllers/PortfolioController.cs` and `.../AlertsController.cs` (apply the attribute to the two POSTs)
- Modify: `src/MarketPulse.Api/Program.cs` (register the filter type in DI: `builder.Services.AddScoped<IdempotencyFilter>();`)
- Test: `tests/MarketPulse.IntegrationTests/IdempotencyTests.cs`

**Interfaces:**
- Produces:
  - `IdempotencyKey` entity: `Guid Id`, `Guid UserId`, `string Endpoint`, `string Key`, `string RequestHash`, `int? ResponseStatusCode`, `string? ResponseBody`, `DateTimeOffset CreatedUtc`. Unique index (`UserId`, `Endpoint`, `Key`). Null response fields = claimed-but-not-completed.
  - `IIdempotencyStore`: `Task<IdempotencyKey?> FindAsync(Guid userId, string endpoint, string key, CancellationToken ct)`, `Task<bool> TryClaimAsync(IdempotencyKey claim, CancellationToken ct)` (returns false on unique-index violation), `Task CompleteAsync(IdempotencyKey claim, int statusCode, string body, CancellationToken ct)`, `Task RemoveAsync(IdempotencyKey claim, CancellationToken ct)`.
  - `[ServiceFilter(typeof(IdempotencyFilter))]` on the two POST actions; header name `Idempotency-Key`.

- [ ] **Step 1: Write the failing integration tests**

`IdempotencyTests.cs`, `[Collection(nameof(SqlServerCollection))]`, using a helper that POSTs a buy with an `Idempotency-Key` header:

```csharp
private static HttpRequestMessage BuyRequest(string key, decimal units = 1m, decimal price = 60m)
{
    var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/portfolio/transactions")
    {
        Content = JsonContent.Create(new { Ticker = "IVV", Side = "Buy", Units = units, Price = price })
    };
    request.Headers.Add("Idempotency-Key", key);
    return request;
}
```

(The CSRF header: `AuthenticatedClient.RegisterAsync` returns a client with the CSRF default header already attached, and default headers apply to `SendAsync` requests too — verify by reading `AttachCsrfHeader`; if it sets a default header on the client, nothing more is needed.)

Facts:

1. `Replaying_a_key_returns_the_stored_response_and_records_one_transaction` — send the same `BuyRequest(key)` twice sequentially; both 201 with byte-identical bodies; `GET /api/v1/portfolio/transactions` shows exactly one row; `GET /api/v1/portfolio` shows 1 unit, not 2.
2. `The_same_key_with_a_different_body_is_422` — `BuyRequest(key, units: 1)`, then `BuyRequest(key, units: 2)` → 422, title `idempotency-key-reuse`.
3. `Keys_are_scoped_per_user` — Alice and Bob each send `BuyRequest("shared-key")`; both 201; each portfolio has exactly one transaction.
4. `A_failed_request_stores_nothing_so_the_key_is_reusable` — sell with no holdings under a key → 422 `insufficient-holdings`; buy first; retry the *same key with the same sell body* → now succeeds (the failure stored nothing).
5. `Two_concurrent_requests_with_one_fresh_key_execute_exactly_once` — `Task.WhenAll` two `SendAsync` of equal `BuyRequest(key)` clones (build two request objects, same key/body); assert exactly one transaction row exists afterwards; each response is either 201 (identical body) or 409 (`idempotency-in-flight` — see filter step 4 below); at least one is 201.
6. `A_request_without_the_header_executes_normally_every_time` — two headerless buys → two transactions (the header is an offer, not a demand).
7. `Post_alerts_honours_the_same_header` — POST `/api/v1/alerts` twice with one key (valid rule body) → one rule in `GET /api/v1/alerts`, identical 201 bodies.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter IdempotencyTests`
Expected: FAIL — test 1 records two transactions (no filter exists).

- [ ] **Step 3: Entity, store, mapping, migration**

`IdempotencyKey.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

/// <summary>
/// One claimed idempotency key. Claimed-first: the row is inserted before the action
/// executes (the unique index is the arbiter under concurrency), completed with the
/// response after success, and removed on failure — a failed request must be safe to
/// retry under the same key. Null response fields mean "claimed, still executing".
/// </summary>
public sealed class IdempotencyKey
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public int? ResponseStatusCode { get; private set; }
    public string? ResponseBody { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }

    private IdempotencyKey() { }

    public IdempotencyKey(Guid userId, string endpoint, string key, string requestHash, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        Endpoint = endpoint;
        Key = key;
        RequestHash = requestHash;
        CreatedUtc = now;
    }

    public bool IsCompleted => ResponseStatusCode is not null;

    public void Complete(int statusCode, string body)
    {
        ResponseStatusCode = statusCode;
        ResponseBody = body;
    }
}
```

DbContext mapping (+ `public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();`):

```csharp
        b.Entity<IdempotencyKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Endpoint).HasMaxLength(128).IsRequired();
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();

            // The arbiter. Two racing requests with one fresh key: the second insert
            // violates this and reads the winner's row instead.
            e.HasIndex(x => new { x.UserId, x.Endpoint, x.Key }).IsUnique();

            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

`IIdempotencyStore.cs` + `IdempotencyStore.cs` — the store uses its own `DbContext` operations but **must save through its own SaveChanges immediately** (claim and completion are their own tiny units of work, deliberately not part of the action's): implement `TryClaimAsync` as Add + `SaveChangesAsync` in try/catch on `DbUpdateException` where the inner exception is a unique-violation (SQL error 2601/2627) → detach the entity, return false. `CompleteAsync` sets and saves. `RemoveAsync` removes and saves. Register scoped in `AddPersistence`.

**Important seam:** the store must not share the request's scoped `MarketPulseDbContext` — a claim that sits as a pending Add on the action's context would commit with the action instead of before it, destroying claim-first semantics, and the action's own SaveChanges would sweep it up. Inject `IDbContextFactory<MarketPulseDbContext>` instead: `builder.Services.AddDbContextFactory<MarketPulseDbContext>(...)` is additive and coexists with `AddDbContext` (both registrations share the options); the store creates and disposes its own context per call. Note this in the store's doc comment — it is the one place in the codebase a second context is deliberate.

Generate migration `Slice5aIdempotencyKeys`; `dotnet ef database update`.

- [ ] **Step 4: The filter**

`IdempotencyFilter.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketPulse.Api.Middleware;
using MarketPulse.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MarketPulse.Api.Filters;

/// <summary>
/// Stored-key idempotency for POSTs that opt in via [ServiceFilter]. Claimed-first: the
/// key row is inserted before the action runs, so two racing requests cannot both
/// execute — the unique index picks the winner and the loser answers 409 until the
/// winner's response is stored. Failures release the claim: a 4xx/5xx stores nothing and
/// the same key may be retried. The hash is over the bound action arguments, not the raw
/// body, so formatting differences don't defeat replay while a genuinely different
/// request under a reused key is caught.
/// </summary>
public sealed class IdempotencyFilter(IIdempotencyStore store, ICurrentUser user)
    : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var values)
            || values.ToString() is not { Length: > 0 and <= 128 } key)
        {
            await next(); // the header is an offer, not a demand
            return;
        }

        var endpoint = $"{context.HttpContext.Request.Method} {context.ActionDescriptor.AttributeRouteInfo?.Template}";
        var hash = HashArguments(context.ActionArguments);

        var existing = await store.FindAsync(user.UserId, endpoint, key, context.HttpContext.RequestAborted);

        if (existing is not null)
        {
            if (existing.RequestHash != hash)
            {
                context.Result = Problem(422, "idempotency-key-reuse",
                    "This Idempotency-Key was already used with a different request.");
                return;
            }

            if (!existing.IsCompleted)
            {
                context.Result = Problem(409, "idempotency-in-flight",
                    "The original request with this key is still executing. Retry shortly.");
                return;
            }

            context.Result = new ContentResult
            {
                StatusCode = existing.ResponseStatusCode,
                Content = existing.ResponseBody,
                ContentType = "application/json"
            };
            return;
        }

        var claim = new Domain.Entities.IdempotencyKey(
            user.UserId, endpoint, key, hash, DateTimeOffset.UtcNow);

        if (!await store.TryClaimAsync(claim, context.HttpContext.RequestAborted))
        {
            // Lost the race for a fresh key. The winner is executing or done; re-reading
            // now either replays or reports in-flight — one recursive pass handles both.
            await OnActionExecutionAsync(context, next);
            return;
        }

        try
        {
            var executed = await next();

            if (executed.Exception is not null && !executed.ExceptionHandled)
            {
                await store.RemoveAsync(claim, CancellationToken.None);
                return; // the middleware turns the exception into its ProblemDetails
            }

            if (executed.Result is ObjectResult { StatusCode: null or >= 200 and < 300 } ok)
            {
                var body = JsonSerializer.Serialize(
                    ok.Value, context.HttpContext.RequestServices
                        .GetRequiredService<Microsoft.Extensions.Options.IOptions<JsonOptions>>()
                        .Value.JsonSerializerOptions);
                await store.CompleteAsync(
                    claim, ok.StatusCode ?? StatusCodes.Status200OK, body, CancellationToken.None);
            }
            else
            {
                // Non-2xx result (validation short-circuit, 4xx ObjectResult): release.
                await store.RemoveAsync(claim, CancellationToken.None);
            }
        }
        catch
        {
            await store.RemoveAsync(claim, CancellationToken.None);
            throw;
        }
    }

    private static string HashArguments(IDictionary<string, object?> arguments)
    {
        var canonical = JsonSerializer.Serialize(
            arguments.OrderBy(a => a.Key).ToDictionary(a => a.Key, a => a.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static ObjectResult Problem(int status, string code, string detail) =>
        new(new ProblemDetails
        {
            Status = status,
            Title = code,
            Type = $"https://marketpulse.local/errors/{code}",
            Detail = detail
        })
        { StatusCode = status };
}
```

Implementation notes for whoever builds this:
- `CancellationToken.None` on Complete/Remove is deliberate: once the action has committed, the bookkeeping must finish even if the client disconnected.
- The replay `ContentResult` reproduces the stored 201's body but not its `Location` header; acceptable — record it in ADR-004's consequences rather than storing headers speculatively.
- The `CreatedAtAction` result is an `ObjectResult` subclass, so the 2xx branch catches it; confirm `ok.StatusCode` is 201 there (CreatedAtActionResult sets it).
- Serialize with the app's configured `JsonOptions` (camelCase) so the stored body is byte-identical to what the first response sent through the normal pipeline. Verify test 1's "byte-identical" assertion actually holds; if the framework's serialization of the live response differs, serialize the stored body the same way the test observes and adjust — the invariant that matters is identical bytes on the wire both times.
- Apply with `[ServiceFilter(typeof(IdempotencyFilter))]` on `PortfolioController.Record` and `AlertsController.Create`; register `builder.Services.AddScoped<IdempotencyFilter>();` in `Program.cs` near the other service registrations.
- If `ICurrentUser` lives in `MarketPulse.Application.Abstractions`, fix the using accordingly (check `AlertsController`'s usings for where it comes from — the filter runs after auth, so `user.UserId` is safe on these `[Authorize]` endpoints).
- Known limitation, named rather than solved: a hard process crash between claim and completion orphans the claim row, and that key then answers 409 `idempotency-in-flight` forever — the client's remedy is a fresh key. Reclaiming stale claims needs a timestamp policy that belongs with key retention (both deliberately deferred); this goes in ADR-004's consequences, not in code.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter IdempotencyTests`
Expected: all 7 PASS. Then the full integration suite (`dotnet test tests/MarketPulse.IntegrationTests`) — the alerts suites must be untouched by the opt-in filter.

- [ ] **Step 6: Commit**

```bash
git add src/MarketPulse.Domain src/MarketPulse.Application src/MarketPulse.Infrastructure src/MarketPulse.Api tests/MarketPulse.IntegrationTests
git commit -m "feat(api): stored-key idempotency on transaction and alert creation"
```

---

### Task 6: The anomaly pair

**Files:**
- Create: `tests/MarketPulse.IntegrationTests/PortfolioConcurrencyAnomalyTests.cs`

**Interfaces:**
- Consumes: `SqlServerFixture.CreateContext()`, the Task 2 mapping, `AuthenticatedClient`/`TestFactory` for user + API-path setup.

- [ ] **Step 1: Write the pair**

The README's "serializable vs read-committed demonstrated in the mock order placement flow, with anomaly tests," in executable form. One test class, two facts, extensively commented — these tests are documentation:

```csharp
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// The pair that makes ADR-004's isolation discussion executable. Under read-committed —
/// SQL Server's default, and what every request here runs at — two interleaved
/// read-validate-write sequences on the same holding are a classic lost-update: both
/// validate against the same snapshot, both commit, and the second silently erases the
/// first's effect. Test (a) reproduces exactly that by stripping the one defence the
/// system has; test (b) shows the defence — the RowVersion token — turning the same
/// interleaving into a 409 for the loser. Serializable isolation would also prevent it,
/// at the cost of deadlock-retry plumbing on every write; the token localises the cost
/// to the one aggregate that races. See ADR-004.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public class PortfolioConcurrencyAnomalyTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Without_the_token_two_racing_sells_oversell_the_holding()
    {
        var portfolioId = await SeedPortfolioAsync(units: 10m);

        // Both contexts load the same state: 10 units held.
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var copy1 = await first.Portfolios.SingleAsync(p => p.Id == portfolioId);
        var copy2 = await second.Portfolios.SingleAsync(p => p.Id == portfolioId);

        // Both validate 8 <= 10 and pass. This is the read-validate-write interleaving.
        copy1.RecordSell("IVV", 8m, 60m, Now, Now);
        copy2.RecordSell("IVV", 8m, 60m, Now, Now);

        await first.SaveChangesAsync();

        // The bypass: hand the second context the winner's current token, so its UPDATE's
        // WHERE clause matches — this is what every write would behave like if the token
        // did not exist. Read-committed alone does not save you: each statement saw only
        // committed data, and the anomaly happened anyway.
        var currentVersion = await first.Portfolios
            .Where(p => p.Id == portfolioId)
            .Select(p => p.RowVersion)
            .SingleAsync();

        var rootEntry = second.Entry(copy2); // token lives on the aggregate root
        rootEntry.Property(nameof(Portfolio.RowVersion)).OriginalValue = currentVersion;

        await second.SaveChangesAsync(); // commits — the lost update

        await using var check = fixture.CreateContext();
        var holding = (await check.Portfolios.SingleAsync(p => p.Id == portfolioId))
            .Holdings.Single();

        // 16 units sold from a holding of 10. The anomaly, asserted.
        Assert.True(holding.Units < 0,
            $"Expected the oversell anomaly (negative units); got {holding.Units}.");
    }

    [Fact]
    public async Task With_the_token_the_second_sell_loses_with_a_concurrency_exception()
    {
        var portfolioId = await SeedPortfolioAsync(units: 10m);

        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var copy1 = await first.Portfolios.SingleAsync(p => p.Id == portfolioId);
        var copy2 = await second.Portfolios.SingleAsync(p => p.Id == portfolioId);

        copy1.RecordSell("IVV", 8m, 60m, Now, Now);
        copy2.RecordSell("IVV", 8m, 60m, Now, Now);

        await first.SaveChangesAsync();

        // The real path: stale token, UPDATE matches no row, EF throws — the API's
        // middleware turns this into the 409 the client retries on.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var check = fixture.CreateContext();
        Assert.Equal(2m, (await check.Portfolios.SingleAsync(p => p.Id == portfolioId))
            .Holdings.Single().Units);
    }

    private async Task<Guid> SeedPortfolioAsync(decimal units)
    {
        await using var factory = TestFactory.Create(fixture);
        var email = AuthenticatedClient.NewEmail();
        _ = await AuthenticatedClient.RegisterAsync(factory, email);

        await using var db = fixture.CreateContext();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();

        var portfolio = Portfolio.Create(userId);
        var tx = portfolio.RecordBuy("IVV", units, 60m, Now, Now);
        db.Portfolios.Add(portfolio);
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();
        return portfolio.Id;
    }
}
```

Implementation caveats to verify while making this pass:
- The token bypass in test (a) manipulates `OriginalValue` of `RowVersion` on the **aggregate root's** entry. If the holding change alone doesn't touch the root row, EF may not include the root in the UPDATE at all — in that case the token never enters the holding's own UPDATE and the bypass needs nothing (the anomaly happens because owned-entity updates don't carry the root's token in their WHERE clause). **Establish the actual behaviour first**: write test (b) first; if (b) fails because EF does *not* throw (the sell only updates the Holdings row and never checks the root's token), then the domain's concurrency story has a real hole — stop and report it (the fix is forcing a root touch per trade, e.g. a `LastTradedUtc` property on `Portfolio` set by `RecordBuy`/`RecordSell`, which puts the root row — and its token check — into every trade's unit of work; that property then needs mapping and migration in Task 2's files and a note in the spec's domain section). This caveat is the task's real work; the code above is the destination.
- Whichever mechanism emerges, test (a) must genuinely commit both sells and observe negative units, and test (b) must throw through the unmodified real path.

- [ ] **Step 2: Run, iterate per the caveat, verify both pass**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter PortfolioConcurrencyAnomalyTests`
Expected: both PASS, with the caveat above resolved and documented in code comments (and, if the `LastTradedUtc` touch proved necessary, in the commit message).

- [ ] **Step 3: Commit**

```bash
git add tests/MarketPulse.IntegrationTests src/MarketPulse.Domain src/MarketPulse.Infrastructure
git commit -m "test(portfolio): the oversell anomaly pair — reproduced without the token, prevented with it"
```

---

### Task 7: ADR-004 and docs reconciliation

**Files:**
- Create: `docs/adr/004-cqrs-scope.md`
- Modify: `README.md`, `docs/MarketPulse-Pro-README.md` (idempotency, isolation, CQRS, portfolio rows)
- Modify: `docs/ROADMAP.md` (5a landed; 5b next; phase-1 table row)
- Modify: `docs/TESTING.md` (anomaly pair, idempotency tests, portfolio suites)

- [ ] **Step 1: Write ADR-004**

Read `docs/adr/009-messaging-architecture.md` and `docs/adr/007-state-architecture.md` first; mirror their heading conventions. Content it must carry:

- **Decision:** CQRS scope is MediatR commands/queries over one store. Writes go through the `Portfolio` aggregate (invariants, RowVersion); reads that would force the aggregate to hold unbounded state (transaction history) go straight to the table. No separate read model, no projections, no event sourcing.
- **Rejected:** full CQRS with a read store (nothing here has divergent read/write shapes or scale); event-sourced portfolio (the transaction table IS the event log in practice, without the replay machinery); handler-per-file-per-layer ceremony beyond what the codebase already does.
- **Consequences, stated honestly:** append-only corrections (offsetting trades, no delete/amend); replayed responses reproduce the stored body but not per-response headers (Location); idempotency keys are never expired, and a claim orphaned by a hard crash leaves its key permanently in-flight (fresh key is the remedy — retention and stale-claim reclaim are one deferred ops concern); the isolation-level demonstration lives in `PortfolioConcurrencyAnomalyTests`, not in production transaction options — and why (the RowVersion decision from the spec's decision table, summarised).
- Cross-reference the 5a spec and the anomaly test class by name.

- [ ] **Step 2: Reconcile the READMEs, ROADMAP, TESTING**

- `grep -n -i "idempoten\|serializable\|read-committed\|CQRS\|portfolio" README.md docs/MarketPulse-Pro-README.md` — every claim either now points at built code (idempotency keys: built, two endpoints; isolation: the anomaly pair; CQRS: ADR-004; portfolio: backend built, UI 5b) or is rephrased to say exactly what exists. The mock-portfolio feature row updates to name what 5a shipped and that the dashboard surface is 5b.
- `docs/ROADMAP.md`: slice 5 splits into 5a (done, with a one-line summary naming the anomaly pair and idempotency) and 5b (next: dashboard surface, unrealised P&L client-side, buy/sell form sending idempotency keys, Playwright journey); phase-1 row updated; the `docs/adr/004-cqrs-scope.md` debt-table row resolved.
- `docs/TESTING.md`: portfolio unit tests, persistence, API, idempotency, and the anomaly pair get their rows/sections in the file's existing structure — the anomaly pair especially, described as the executable isolation discussion.

- [ ] **Step 3: Verify and commit**

Re-read each edited section for internal consistency. Then:

```bash
git add docs README.md
git commit -m "docs: ADR-004 CQRS scope; reconcile README claims with the built portfolio backend"
```

---

### Task 8: Full verification

- [ ] **Step 1: Clean-slate build and test, every suite**

```bash
pnpm -r build && pnpm -r test -- --run
dotnet build MarketPulse.sln && dotnet test
pkill -f MarketPulse.Alerts 2>/dev/null; pnpm --dir tests/e2e exec playwright test
```

Expected: everything green — the frontend and e2e suites must be untouched by this backend slice (the e2e run is the regression proof, not new coverage).

- [ ] **Step 2: Spec done-criteria walkthrough**

Check the spec's six done criteria against captured evidence (superpowers:verification-before-completion): (1) suites green; (2) buy + partial sell end-to-end assertion (PortfolioApiTests test 1); (3) the anomaly pair; (4) idempotency replay on both endpoints; (5) ADR-004 + README claims; (6) `DependencyRuleTests` green. Also `git log --format=%B test..HEAD | grep -ic co-authored` → 0.

- [ ] **Step 3: Finish the branch**

Use superpowers:finishing-a-development-branch — merge `feature/slice-5a-portfolio-backend` into `test` per the promotion flow.
