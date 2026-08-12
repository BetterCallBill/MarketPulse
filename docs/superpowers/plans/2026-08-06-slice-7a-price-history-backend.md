# Slice 7a — Price History Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the live tick stream into a bounded `PriceTicks` table and serve OHLC candles and watchlist sparklines from it through Dapper — including the deliberate N+1 (introduced, measured, fixed → ADR-006 + `docs/sql/`).

**Architecture:** A new `PersistingTickSink` enqueues into its own bounded buffer beside the existing SignalR/RabbitMQ sinks; `TickPersistenceService` flushes batches via a set-based `INSERT` (scope-per-flush, never `AddRange` — see spec decision 2); `TickRetentionService` keeps the table bounded. Reads are the codebase's first Dapper code behind `IPriceHistoryReader`, exposed by two `[Authorize]` endpoints on a new `PricesController`.

**Tech Stack:** .NET 10 / ASP.NET Core 10, EF Core 10 (write side, migration), Dapper (read side, new), SQL Server 2022, MediatR 12.5, FluentValidation 12.1, xUnit + NSubstitute + `FakeTimeProvider`, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-06-price-history-backend-design.md` — read it first; decisions and error slugs there are binding.

## Global Constraints

- `TreatWarningsAsErrors` is on solution-wide: the build must finish with **0 warnings**.
- Central package management: every version goes in `Directory.Packages.props`, never in a `.csproj`.
- Commits are conventional (`feat:`, `fix:`, `test:`, `docs:`), one change each, **no Co-Authored-By trailer** (repo rule).
- TDD per task: failing test → minimal implementation → green → commit.
- Branch: all work on `feature/slice-7a-price-history`, merged to `test` only after the full local gate (Task 12).
- `Domain` gains no package or project references (`DependencyRuleTests` enforces); Dapper appears **only** in Infrastructure.
- Integration tests share one database: **every test seeds its own synthetic tickers** (codes starting `Z`, absent from `SeedData`) except where a seeded ticker (`IVV`, `NDQ`, `VAS`) is explicitly required. Other test classes boot the Fake tick service, so `PriceTicks` accumulates real-ticker rows as a side effect — never assert on totals for seeded tickers.
- Timestamp columns are `datetimeoffset`, matching every existing table (the spec's sketch said `datetime2`; the PK + `IGNORE_DUP_KEY` are the load-bearing parts, the column type follows repo convention — note this in the task-N report if a reviewer asks).

## File Structure

```
src/MarketPulse.Application/
  Abstractions/ISqlConnectionFactory.cs      (new — Dapper's connection seam)
  Abstractions/IPriceHistoryReader.cs        (new — candles + sparklines, Application DTO: Candle)
  Configuration/HistoryOptions.cs            (new)
  PriceHistory/GetCandlesQuery.cs            (new — DTOs, validator, handler, CandleIntervals)
  PriceHistory/GetSparklinesQuery.cs         (new — DTO, handler; v1 loop then set-based)
src/MarketPulse.Domain/
  Exceptions/PriceHistoryExceptions.cs       (new — TickerNotFoundException, 404)
src/MarketPulse.Infrastructure/
  Persistence/SqlConnectionFactory.cs        (new)
  Persistence/MarketPulseDbContext.cs        (modify — PriceTickRow mapping + DbSet)
  Persistence/Migrations/*Slice7aPriceTicks* (generated, then edited for IGNORE_DUP_KEY)
  History/PriceTickRow.cs                    (new)
  History/TickBuffer.cs                      (new)
  History/PersistingTickSink.cs              (new)
  History/IPriceTickBatchWriter.cs           (new)
  History/SqlPriceTickBatchWriter.cs         (new)
  History/TickPersistenceService.cs          (new)
  History/TickRetentionService.cs            (new)
  History/DapperPriceHistoryReader.cs        (new)
  DependencyInjection.cs                     (modify — registrations)
src/MarketPulse.Api/
  Controllers/PricesController.cs            (new)
  Program.cs                                 (modify — HistoryOptions binding)
tests/MarketPulse.UnitTests/History/         (new folder — buffer, sink, persistence-service tests)
tests/MarketPulse.IntegrationTests/          (new files per task; TestFactory.cs modified once)
docs/sql/                                    (new — seed + before/after measurement scripts and outputs)
docs/adr/006-n-plus-one-postmortem.md        (new)
docs/ROADMAP.md                              (modify — closing commit)
```

---

### Task 1: Branch, Dapper dependency, connection factory

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj`
- Create: `src/MarketPulse.Application/Abstractions/ISqlConnectionFactory.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/SqlConnectionFactory.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (`AddPersistence`)
- Modify: `tests/MarketPulse.IntegrationTests/TestFactory.cs`
- Test: `tests/MarketPulse.IntegrationTests/SqlConnectionFactoryTests.cs`

**Interfaces:**
- Consumes: `AddPersistence(this IServiceCollection, string connectionString)` (existing).
- Produces: `ISqlConnectionFactory { Task<DbConnection> OpenAsync(CancellationToken ct); }` — every later Dapper read goes through this; Task 11's counting decorator wraps it.

- [ ] **Step 1: Create the branch**

```bash
git checkout test && git pull && git checkout -b feature/slice-7a-price-history
```

- [ ] **Step 2: Pin Dapper (and Microsoft.Data.SqlClient if not already transitive-compatible)**

In `Directory.Packages.props`, add alphabetically within the `<ItemGroup>`:

```xml
<PackageVersion Include="Dapper" Version="2.1.66" />
```

Then check whether `Microsoft.Data.SqlClient` needs a direct pin:

```bash
grep -n 'Microsoft.Data.SqlClient' Directory.Packages.props || \
  dotnet list src/MarketPulse.Infrastructure package --include-transitive | grep Microsoft.Data.SqlClient
```

`SqlConnectionFactory` compiles against the transitive `Microsoft.Data.SqlClient` that EF Core SqlServer brings; only if the build in Step 6 fails with a missing-reference error, add a direct `<PackageVersion>` + `<PackageReference>` pinned to the **exact transitive version** the command above prints (never a different one — a downgrade warning is a build failure here).

In `src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj`, add to the existing `<ItemGroup>` of `PackageReference`s:

```xml
<PackageReference Include="Dapper" />
```

- [ ] **Step 3: Write the failing smoke test**

`tests/MarketPulse.IntegrationTests/SqlConnectionFactoryTests.cs`:

```csharp
using Dapper;
using MarketPulse.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class SqlConnectionFactoryTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Opens_a_connection_to_the_test_database()
    {
        using var factory = TestFactory.Create(fixture);
        var connections = factory.Services.GetRequiredService<ISqlConnectionFactory>();

        await using var connection = await connections.OpenAsync(CancellationToken.None);
        var one = await connection.QuerySingleAsync<int>("SELECT 1");

        Assert.Equal(1, one);
    }
}
```

Add `<PackageReference Include="Dapper" />` to `tests/MarketPulse.IntegrationTests/MarketPulse.IntegrationTests.csproj` too (tests call Dapper directly here and in Task 8).

- [ ] **Step 4: Run it to verify it fails**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~SqlConnectionFactoryTests"
```

Expected: compile failure — `ISqlConnectionFactory` does not exist.

- [ ] **Step 5: Implement**

`src/MarketPulse.Application/Abstractions/ISqlConnectionFactory.cs`:

```csharp
using System.Data.Common;

namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Hands out open connections for the Dapper read path. An abstraction so tests can count
/// or intercept connections (the N+1 pin in this slice does exactly that). EF Core's
/// DbContext does not go through this.
/// </summary>
public interface ISqlConnectionFactory
{
    Task<DbConnection> OpenAsync(CancellationToken ct);
}
```

`src/MarketPulse.Infrastructure/Persistence/SqlConnectionFactory.cs`:

```csharp
using System.Data.Common;
using MarketPulse.Application.Abstractions;
using Microsoft.Data.SqlClient;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class SqlConnectionFactory(string connectionString) : ISqlConnectionFactory
{
    public async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
```

In `AddPersistence` (`DependencyInjection.cs`), after the `AddDbContext` calls:

```csharp
services.AddSingleton<ISqlConnectionFactory>(new SqlConnectionFactory(connectionString));
```

In `TestFactory.Create`, add one entry to the `settings` dictionary (above the auth line):

```csharp
// The Dapper connection factory is built from the connection string Program.cs reads
// off configuration — unlike the DbContextOptions replacement below, it cannot be
// swapped after the fact, so the configuration itself must point at the container.
["ConnectionStrings:MarketPulse"] = fixture.ConnectionString,
```

- [ ] **Step 6: Run to verify green, and that nothing else broke**

```bash
dotnet build -c Release && dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~SqlConnectionFactoryTests"
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~DependencyRuleTests"
```

Expected: PASS (architecture test proves Domain untouched; `ISqlConnectionFactory` uses only BCL types so Application stays clean).

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(history): Dapper dependency and ISqlConnectionFactory seam"
```

---

### Task 2: HistoryOptions with startup validation

**Files:**
- Create: `src/MarketPulse.Application/Configuration/HistoryOptions.cs`
- Modify: `src/MarketPulse.Api/Program.cs`
- Test: `tests/MarketPulse.IntegrationTests/HistoryOptionsTests.cs`

**Interfaces:**
- Produces: `HistoryOptions { SectionName = "History"; int RetentionDays=7; int FlushIntervalSeconds=5; int FlushBatchSize=500; int BufferCapacity=5000; int MaxCandleBuckets=1000; int SparklineWindowMinutes=60; int RetentionSweepIntervalSeconds=3600; int RetentionDeleteChunk=10000; }` — consumed by every later task via `IOptions<HistoryOptions>`.

- [ ] **Step 1: Write the failing test**

`tests/MarketPulse.IntegrationTests/HistoryOptionsTests.cs`:

```csharp
namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class HistoryOptionsTests(SqlServerFixture fixture)
{
    [Fact]
    public void Zero_retention_days_fails_startup_validation()
    {
        using var factory = TestFactory.Create(fixture, new Dictionary<string, string?>
        {
            ["History:RetentionDays"] = "0"
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("HistoryOptions", ex.ToString());
    }

    [Fact]
    public void Defaults_are_valid_without_any_History_section()
    {
        using var factory = TestFactory.Create(fixture);
        using var client = factory.CreateClient(); // boots; would throw if defaults invalid
    }
}
```

- [ ] **Step 2: Run to verify the first test fails** (no validation exists yet, so `CreateClient` succeeds and `ThrowsAny` fails)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~HistoryOptionsTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Application/Configuration/HistoryOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

public sealed class HistoryOptions
{
    public const string SectionName = "History";

    [Range(1, 365)]
    public int RetentionDays { get; init; } = 7;

    [Range(1, 3600)]
    public int FlushIntervalSeconds { get; init; } = 5;

    /// <summary>≤ 600: each row costs 3 parameters and SQL Server caps a command at 2,100.</summary>
    [Range(1, 600)]
    public int FlushBatchSize { get; init; } = 500;

    [Range(100, 100_000)]
    public int BufferCapacity { get; init; } = 5000;

    [Range(10, 10_000)]
    public int MaxCandleBuckets { get; init; } = 1000;

    [Range(1, 1440)]
    public int SparklineWindowMinutes { get; init; } = 60;

    [Range(1, 86_400)]
    public int RetentionSweepIntervalSeconds { get; init; } = 3600;

    [Range(1000, 100_000)]
    public int RetentionDeleteChunk { get; init; } = 10_000;
}
```

In `Program.cs`, directly after the `MarketDataOptions` binding block (line ~42), mirroring it exactly:

```csharp
builder.Services.AddOptions<HistoryOptions>()
    .Bind(builder.Configuration.GetSection(HistoryOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

- [ ] **Step 4: Run to verify both tests pass**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~HistoryOptionsTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(history): HistoryOptions bound with startup validation"
```

---

### Task 3: PriceTicks table — row type, mapping, migration with IGNORE_DUP_KEY

**Files:**
- Create: `src/MarketPulse.Infrastructure/History/PriceTickRow.cs`
- Modify: `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`
- Create (scaffolded then edited): `src/MarketPulse.Infrastructure/Persistence/Migrations/*_Slice7aPriceTicks.cs`
- Test: `tests/MarketPulse.IntegrationTests/PriceTickTableTests.cs`

**Interfaces:**
- Produces: `PriceTickRow { string Ticker; DateTimeOffset TimestampUtc; decimal Price; }` in `MarketPulse.Infrastructure.History`, and `DbSet<PriceTickRow> PriceTicks` on `MarketPulseDbContext`. Table `PriceTicks(Ticker nvarchar(8), TimestampUtc datetimeoffset, Price decimal(18,4))`, clustered PK `(Ticker, TimestampUtc)` with `IGNORE_DUP_KEY = ON`. **No FK to `Tickers`** — history is observational and must survive reference-data edits; unknown-ticker filtering happens at the API layer.

- [ ] **Step 1: Write the failing test**

`tests/MarketPulse.IntegrationTests/PriceTickTableTests.cs`:

```csharp
using MarketPulse.Infrastructure.History;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PriceTickTableTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Replayed_tick_is_ignored_not_an_error()
    {
        await using var db = fixture.CreateContext();
        const string insert =
            "INSERT INTO PriceTicks (Ticker, TimestampUtc, Price) " +
            "VALUES ('ZDUP', '2026-08-06T00:00:00+00:00', 10.5)";

        var first = await db.Database.ExecuteSqlRawAsync(insert);
        var second = await db.Database.ExecuteSqlRawAsync(insert); // replay: must not throw

        Assert.Equal(1, first);
        Assert.Equal(0, second); // IGNORE_DUP_KEY: silently dropped
        Assert.Equal(1, await db.PriceTicks.CountAsync(t => t.Ticker == "ZDUP"));
    }
}
```

- [ ] **Step 2: Run to verify it fails** (compile error: no `PriceTickRow`, no `PriceTicks` DbSet)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~PriceTickTableTests"
```

- [ ] **Step 3: Implement the row type and mapping**

`src/MarketPulse.Infrastructure/History/PriceTickRow.cs`:

```csharp
namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The persistence row for one observed tick. Infrastructure-only: it exists so EF can
/// model and migrate the table (and tests can query it); the write path itself uses raw
/// SQL, and Domain's PriceTick value object is deliberately unrelated.
/// </summary>
public sealed class PriceTickRow
{
    public required string Ticker { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public required decimal Price { get; init; }
}
```

In `MarketPulseDbContext.cs`: add `using MarketPulse.Infrastructure.History;`, a DbSet beside the existing ones:

```csharp
public DbSet<PriceTickRow> PriceTicks => Set<PriceTickRow>();
```

and in `OnModelCreating`, after the last existing entity block:

```csharp
b.Entity<PriceTickRow>(e =>
{
    e.ToTable("PriceTicks");
    // Clustered composite PK: every read is "one ticker, a time range", so range scans
    // arrive pre-sorted with no secondary index to maintain. IGNORE_DUP_KEY is added by
    // raw SQL in the migration — EF cannot express it.
    e.HasKey(x => new { x.Ticker, x.TimestampUtc }).IsClustered();
    e.Property(x => x.Ticker).HasMaxLength(8);
    e.Property(x => x.Price).HasColumnType("decimal(18,4)");
});
```

- [ ] **Step 4: Scaffold the migration, then edit it**

```bash
dotnet ef migrations add Slice7aPriceTicks --project src/MarketPulse.Infrastructure --startup-project src/MarketPulse.Api
```

In the generated `*_Slice7aPriceTicks.cs`, append to the end of `Up(...)` (after `CreateTable`):

```csharp
// EF cannot express IGNORE_DUP_KEY; rebuild the PK with it. A replayed
// (Ticker, TimestampUtc) — Yahoo re-serving an observation across polls — then becomes
// a no-op at the database instead of an exception poisoning a whole flush batch.
migrationBuilder.Sql("""
    ALTER TABLE [PriceTicks] DROP CONSTRAINT [PK_PriceTicks];
    ALTER TABLE [PriceTicks] ADD CONSTRAINT [PK_PriceTicks]
        PRIMARY KEY CLUSTERED ([Ticker], [TimestampUtc]) WITH (IGNORE_DUP_KEY = ON);
    """);
```

(`Down` already drops the table; no edit needed there.)

- [ ] **Step 5: Run to verify green**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~PriceTickTableTests"
```

- [ ] **Step 6: Apply to the dev database so local `dotnet run` keeps working**

```bash
docker compose up -d sqlserver && dotnet ef database update --project src/MarketPulse.Infrastructure --startup-project src/MarketPulse.Api
```

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(history): PriceTicks table, clustered natural key, IGNORE_DUP_KEY dedupe"
```

---

### Task 4: TickBuffer and PersistingTickSink

**Files:**
- Create: `src/MarketPulse.Infrastructure/History/TickBuffer.cs`
- Create: `src/MarketPulse.Infrastructure/History/PersistingTickSink.cs`
- Test: `tests/MarketPulse.UnitTests/History/TickBufferTests.cs`

**Interfaces:**
- Consumes: `ITickSink { Task SendAsync(PriceTick tick, CancellationToken ct); }` (existing), `HistoryOptions.BufferCapacity` (Task 2).
- Produces: `TickBuffer { ChannelWriter<PriceTick> Writer; ChannelReader<PriceTick> Reader; }` — Task 6's service drains `Reader`.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.UnitTests/History/TickBufferTests.cs`:

```csharp
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.History;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketPulse.UnitTests.History;

public class TickBufferTests
{
    private static TickBuffer CreateBuffer(int capacity) =>
        new(Options.Create(new HistoryOptions { BufferCapacity = capacity }),
            NullLogger<TickBuffer>.Instance);

    private static PriceTick Tick(decimal price) =>
        new("IVV", price, new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero).AddSeconds((double)price));

    [Fact]
    public async Task Sink_enqueues_without_blocking()
    {
        var buffer = CreateBuffer(capacity: 100);
        var sink = new PersistingTickSink(buffer);

        await sink.SendAsync(Tick(1m), CancellationToken.None);

        Assert.True(buffer.Reader.TryRead(out var tick));
        Assert.Equal(1m, tick.Price);
    }

    [Fact]
    public async Task Overflow_drops_the_oldest_tick()
    {
        var buffer = CreateBuffer(capacity: 100); // Channel enforces a real minimum of 1; we fill past it
        var sink = new PersistingTickSink(buffer);

        for (var i = 0; i < 103; i++)
        {
            await sink.SendAsync(Tick(i), CancellationToken.None);
        }

        Assert.True(buffer.Reader.TryRead(out var oldestSurvivor));
        Assert.Equal(3m, oldestSurvivor.Price); // 0, 1, 2 were dropped
    }
}
```

- [ ] **Step 2: Run to verify they fail** (compile error: types missing)

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TickBufferTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Infrastructure/History/TickBuffer.cs`:

```csharp
using System.Threading.Channels;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The write path's own buffer, separate from PriceTickChannel: the broadcast loop must
/// never wait on the database, so the sink enqueues here and TickPersistenceService drains
/// on its own clock. DropOldest under overflow — a sustained database outage costs a hole
/// in history, never a stalled live stream.
/// </summary>
public sealed class TickBuffer
{
    private readonly Channel<PriceTick> _channel;

    public TickBuffer(IOptions<HistoryOptions> options, ILogger<TickBuffer> logger)
    {
        _channel = Channel.CreateBounded<PriceTick>(
            new BoundedChannelOptions(options.Value.BufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            dropped => logger.LogWarning(
                "Tick buffer full; dropped oldest tick {Ticker}@{TimestampUtc:u}.",
                dropped.Ticker, dropped.TimestampUtc));
    }

    public ChannelWriter<PriceTick> Writer => _channel.Writer;
    public ChannelReader<PriceTick> Reader => _channel.Reader;
}
```

`src/MarketPulse.Infrastructure/History/PersistingTickSink.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The ITickSink that only enqueues. Sinks run sequentially inside TickBroadcaster's loop,
/// so a database write here would tax the SignalR path 25 times a second in Fake mode.
/// </summary>
public sealed class PersistingTickSink(TickBuffer buffer) : ITickSink
{
    public Task SendAsync(PriceTick tick, CancellationToken ct)
    {
        // TryWrite on a DropOldest channel only fails once the channel is completed, which
        // never happens in normal operation; overflow is handled (and logged) by the
        // channel's own drop callback.
        buffer.Writer.TryWrite(tick);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Run to verify green**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TickBufferTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(history): TickBuffer and PersistingTickSink — non-blocking enqueue"
```

---

### Task 5: SqlPriceTickBatchWriter — the set-based INSERT

**Files:**
- Create: `src/MarketPulse.Infrastructure/History/IPriceTickBatchWriter.cs`
- Create: `src/MarketPulse.Infrastructure/History/SqlPriceTickBatchWriter.cs`
- Test: `tests/MarketPulse.IntegrationTests/PriceTickBatchWriterTests.cs`

**Interfaces:**
- Produces: `IPriceTickBatchWriter { Task WriteAsync(IReadOnlyList<PriceTick> batch, CancellationToken ct); }` in `MarketPulse.Infrastructure.History` — Task 6's service resolves it scoped.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.IntegrationTests/PriceTickBatchWriterTests.cs`:

```csharp
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.History;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class PriceTickBatchWriterTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Writes_a_batch_in_one_round_trip()
    {
        await using var db = fixture.CreateContext();
        var writer = new SqlPriceTickBatchWriter(db);

        await writer.WriteAsync(
        [
            new PriceTick("ZBW1", 10m, T0),
            new PriceTick("ZBW1", 11m, T0.AddSeconds(1)),
            new PriceTick("ZBW2", 20m, T0)
        ], CancellationToken.None);

        Assert.Equal(2, await db.PriceTicks.CountAsync(t => t.Ticker == "ZBW1"));
        Assert.Equal(1, await db.PriceTicks.CountAsync(t => t.Ticker == "ZBW2"));
    }

    [Fact]
    public async Task A_replayed_tick_inside_a_batch_does_not_fail_the_batch()
    {
        await using var db = fixture.CreateContext();
        var writer = new SqlPriceTickBatchWriter(db);
        await writer.WriteAsync([new PriceTick("ZBW3", 10m, T0)], CancellationToken.None);

        // one duplicate, one genuinely new — the new one must land
        await writer.WriteAsync(
        [
            new PriceTick("ZBW3", 10m, T0),
            new PriceTick("ZBW3", 12m, T0.AddSeconds(5))
        ], CancellationToken.None);

        Assert.Equal(2, await db.PriceTicks.CountAsync(t => t.Ticker == "ZBW3"));
    }

    [Fact]
    public async Task An_empty_batch_is_a_no_op()
    {
        await using var db = fixture.CreateContext();
        var writer = new SqlPriceTickBatchWriter(db);

        await writer.WriteAsync([], CancellationToken.None); // must not throw
    }
}
```

- [ ] **Step 2: Run to verify they fail** (compile error)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~PriceTickBatchWriterTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Infrastructure/History/IPriceTickBatchWriter.cs`:

```csharp
using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Infrastructure.History;

public interface IPriceTickBatchWriter
{
    /// <summary>Persists one batch. Replayed (Ticker, TimestampUtc) pairs are silently ignored.</summary>
    Task WriteAsync(IReadOnlyList<PriceTick> batch, CancellationToken ct);
}
```

`src/MarketPulse.Infrastructure/History/SqlPriceTickBatchWriter.cs`:

```csharp
using System.Text;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// One parameterised multi-row INSERT per batch, executed through the DbContext's
/// connection. Not AddRange: change tracking buys nothing for append-only rows, and EF's
/// rows-affected propagation check would read IGNORE_DUP_KEY's silently-ignored duplicates
/// as a concurrency failure. Scoped — it follows the DbContext it writes through.
/// </summary>
public sealed class SqlPriceTickBatchWriter(MarketPulseDbContext db) : IPriceTickBatchWriter
{
    public async Task WriteAsync(IReadOnlyList<PriceTick> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // 3 parameters per row; HistoryOptions caps FlushBatchSize at 600, keeping every
        // command under SQL Server's 2,100-parameter limit.
        var sql = new StringBuilder("INSERT INTO PriceTicks (Ticker, TimestampUtc, Price) VALUES ");
        var parameters = new object[batch.Count * 3];

        for (var i = 0; i < batch.Count; i++)
        {
            sql.Append(i == 0 ? "" : ", ").Append($"(@t{i}, @ts{i}, @p{i})");
            parameters[i * 3] = new SqlParameter($"t{i}", batch[i].Ticker);
            parameters[i * 3 + 1] = new SqlParameter($"ts{i}", batch[i].TimestampUtc);
            parameters[i * 3 + 2] = new SqlParameter($"p{i}", batch[i].Price);
        }

        await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, ct);
    }
}
```

- [ ] **Step 4: Run to verify green**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~PriceTickBatchWriterTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(history): set-based batch INSERT writer (IGNORE_DUP_KEY-safe, not AddRange)"
```

---

### Task 6: TickPersistenceService — flush loop, scope-per-flush, wiring

**Files:**
- Create: `src/MarketPulse.Infrastructure/History/TickPersistenceService.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (`AddInfrastructure`)
- Test: `tests/MarketPulse.UnitTests/History/TickPersistenceServiceTests.cs`
- Test: `tests/MarketPulse.IntegrationTests/TickPersistenceIntegrationTests.cs`

**Interfaces:**
- Consumes: `TickBuffer` (Task 4), `IPriceTickBatchWriter` (Task 5), `HistoryOptions` (Task 2).
- Produces: `TickPersistenceService.FlushAsync(CancellationToken ct)` — public test seam, same pattern as `OutboxDispatcher.DispatchPendingAsync`.

- [ ] **Step 1: Write the failing unit tests**

`tests/MarketPulse.UnitTests/History/TickPersistenceServiceTests.cs`:

```csharp
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.History;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MarketPulse.UnitTests.History;

public class TickPersistenceServiceTests
{
    private static PriceTick Tick(decimal price) =>
        new("IVV", price, new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero).AddSeconds((double)price));

    private static (TickPersistenceService Service, TickBuffer Buffer, IPriceTickBatchWriter Writer, FakeTimeProvider Time)
        Create(int flushBatchSize = 500)
    {
        var options = Options.Create(new HistoryOptions { FlushBatchSize = flushBatchSize });
        var buffer = new TickBuffer(options, NullLogger<TickBuffer>.Instance);
        var writer = Substitute.For<IPriceTickBatchWriter>();

        var services = new ServiceCollection();
        services.AddScoped(_ => writer);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var time = new FakeTimeProvider();
        var service = new TickPersistenceService(
            buffer, scopes, options, time, NullLogger<TickPersistenceService>.Instance);
        return (service, buffer, writer, time);
    }

    [Fact]
    public async Task Flush_writes_everything_buffered()
    {
        var (service, buffer, writer, _) = Create();
        buffer.Writer.TryWrite(Tick(1m));
        buffer.Writer.TryWrite(Tick(2m));

        await service.FlushAsync(CancellationToken.None);

        await writer.Received(1).WriteAsync(
            Arg.Is<IReadOnlyList<PriceTick>>(b => b.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Backlog_is_flushed_in_batch_sized_chunks()
    {
        var (service, buffer, writer, _) = Create(flushBatchSize: 2);
        for (var i = 0; i < 5; i++)
        {
            buffer.Writer.TryWrite(Tick(i));
        }

        await service.FlushAsync(CancellationToken.None);

        // 2 + 2 + 1: no single INSERT ever exceeds the parameter budget
        await writer.Received(3).WriteAsync(Arg.Any<IReadOnlyList<PriceTick>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Shutdown_flushes_whatever_remains()
    {
        var (service, buffer, writer, _) = Create();
        await service.StartAsync(CancellationToken.None);
        buffer.Writer.TryWrite(Tick(7m));

        await service.StopAsync(CancellationToken.None);

        await writer.Received().WriteAsync(
            Arg.Is<IReadOnlyList<PriceTick>>(b => b.Count == 1), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify they fail** (compile error)

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TickPersistenceServiceTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Infrastructure/History/TickPersistenceService.cs`:

```csharp
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// Drains TickBuffer on a fixed clock and hands batches to the writer. The service is
/// effectively a singleton, so the scoped writer (and the DbContext under it) is resolved
/// from a fresh scope per flush — constructor injection here would be the classic captive
/// dependency. A failed flush drops that batch and logs: a hole in history is the accepted
/// cost of never letting persistence trouble reach the live stream.
/// </summary>
public sealed class TickPersistenceService(
    TickBuffer buffer,
    IServiceScopeFactory scopes,
    IOptions<HistoryOptions> options,
    TimeProvider timeProvider,
    ILogger<TickPersistenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.FlushIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await FlushAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Tick flush failed; that batch is lost, next interval retries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickPersistenceService stopping; flushing remaining ticks.");
        }

        // The stopping token is already cancelled; give the final batch its own bounded window.
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await FlushAsync(shutdownCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Final tick flush failed; buffered ticks were lost.");
        }
    }

    /// <summary>
    /// Public seam, like OutboxDispatcher.DispatchPendingAsync: tests drive one flush
    /// directly instead of racing the timer. Drains the whole backlog in chunks of
    /// FlushBatchSize so no single INSERT exceeds the parameter budget.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var chunkSize = options.Value.FlushBatchSize;

        while (true)
        {
            var chunk = new List<PriceTick>(chunkSize);
            while (chunk.Count < chunkSize && buffer.Reader.TryRead(out var tick))
            {
                chunk.Add(tick);
            }

            if (chunk.Count == 0)
            {
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var writer = scope.ServiceProvider.GetRequiredService<IPriceTickBatchWriter>();
            await writer.WriteAsync(chunk, ct);

            if (chunk.Count < chunkSize)
            {
                return; // buffer drained
            }
        }
    }
}
```

Add `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />` to `tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj` only if not already present (`grep TimeProvider.Testing tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj` — the market-data tests likely added it).

- [ ] **Step 4: Run to verify unit tests pass**

```bash
dotnet test tests/MarketPulse.UnitTests --filter "FullyQualifiedName~TickPersistenceServiceTests"
```

- [ ] **Step 5: Wire into DI and write the end-to-end failing test**

In `AddInfrastructure` (`DependencyInjection.cs`), after `services.AddSingleton<PriceTickChannel>();`:

```csharp
// Price history write path: the sink only enqueues; the hosted service drains and
// batch-inserts on its own clock (spec 2026-08-06, decision 2).
services.AddSingleton<TickBuffer>();
services.AddSingleton<ITickSink, PersistingTickSink>();
services.AddScoped<IPriceTickBatchWriter, SqlPriceTickBatchWriter>();
services.AddHostedService<TickPersistenceService>();
```

(add `using MarketPulse.Infrastructure.History;` at the top.)

`tests/MarketPulse.IntegrationTests/TickPersistenceIntegrationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class TickPersistenceIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Fake_mode_ticks_reach_the_database()
    {
        using var factory = TestFactory.Create(fixture, new Dictionary<string, string?>
        {
            ["History:FlushIntervalSeconds"] = "1"
        });
        using var client = factory.CreateClient(); // boots the host and its hosted services

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = fixture.CreateContext();
            if (await db.PriceTicks.AnyAsync())
            {
                return; // FakeTickService → channel → broadcaster → sink → buffer → writer → table
            }

            await Task.Delay(250);
        }

        Assert.Fail("No ticks were persisted within 15 seconds of startup.");
    }
}
```

- [ ] **Step 6: Run the integration test — it must pass now that wiring exists; also re-run the SignalR stream test to prove the extra sink didn't disturb the broadcast path**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~TickPersistenceIntegrationTests|FullyQualifiedName~PriceStreamTests"
```

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(history): TickPersistenceService — scope-per-flush batched persistence, wired as a sink"
```

---

### Task 7: TickRetentionService

**Files:**
- Create: `src/MarketPulse.Infrastructure/History/TickRetentionService.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (one line)
- Test: `tests/MarketPulse.IntegrationTests/TickRetentionTests.cs`

**Interfaces:**
- Produces: `TickRetentionService.SweepOnceAsync(CancellationToken ct)` — public test seam.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.IntegrationTests/TickRetentionTests.cs`:

```csharp
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class TickRetentionTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private TickRetentionService CreateService(HistoryOptions options)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext());
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new TickRetentionService(
            scopes, Options.Create(options), new FakeTimeProvider(Now),
            NullLogger<TickRetentionService>.Instance);
    }

    [Fact]
    public async Task Sweep_deletes_expired_rows_and_keeps_recent_ones()
    {
        await using (var db = fixture.CreateContext())
        {
            db.PriceTicks.AddRange(
                new() { Ticker = "ZRET", TimestampUtc = Now.AddDays(-8), Price = 1m },
                new() { Ticker = "ZRET", TimestampUtc = Now.AddDays(-6), Price = 2m });
            await db.SaveChangesAsync();
        }

        await CreateService(new HistoryOptions { RetentionDays = 7 })
            .SweepOnceAsync(CancellationToken.None);

        await using var check = fixture.CreateContext();
        var survivor = Assert.Single(await check.PriceTicks.Where(t => t.Ticker == "ZRET").ToListAsync());
        Assert.Equal(2m, survivor.Price);
    }

    [Fact]
    public async Task Sweep_chunks_until_nothing_old_remains()
    {
        await using (var db = fixture.CreateContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.PriceTicks.Add(new() { Ticker = "ZCHU", TimestampUtc = Now.AddDays(-9).AddSeconds(i), Price = i });
            }
            await db.SaveChangesAsync();
        }

        // chunk of 1000 is the Range floor; drive the loop with the row count instead:
        // 5 expired rows and a 1000-row chunk still exercises the terminate-when-short path,
        // and the count assertion proves the loop deleted everything in one sweep call.
        await CreateService(new HistoryOptions { RetentionDays = 7, RetentionDeleteChunk = 1000 })
            .SweepOnceAsync(CancellationToken.None);

        await using var check = fixture.CreateContext();
        Assert.False(await check.PriceTicks.AnyAsync(t => t.Ticker == "ZCHU"));
    }
}
```

(EF `AddRange`/`SaveChangesAsync` is fine for *seeding* — these rows are unique, so `IGNORE_DUP_KEY` never fires and the rows-affected check holds. The prohibition is on the production flush path, where replays are expected.)

- [ ] **Step 2: Run to verify they fail** (compile error)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~TickRetentionTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Infrastructure/History/TickRetentionService.cs`:

```csharp
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// Keeps PriceTicks bounded: rows older than RetentionDays are deleted on an hourly clock,
/// in chunks, so no single DELETE holds locks long enough to matter. The debt RefreshTokens
/// still owes (INTERVIEW-QA Q8.5), not repeated here.
/// </summary>
public sealed class TickRetentionService(
    IServiceScopeFactory scopes,
    IOptions<HistoryOptions> options,
    TimeProvider timeProvider,
    ILogger<TickRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.RetentionSweepIntervalSeconds);
        using var timer = new PeriodicTimer(interval, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retention sweep failed; next interval retries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickRetentionService stopping.");
        }
    }

    /// <summary>Public seam for tests, like OutboxDispatcher.DispatchPendingAsync.</summary>
    public async Task SweepOnceAsync(CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.RetentionDays);
        var chunk = options.Value.RetentionDeleteChunk;
        var total = 0;
        int deleted;

        do
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketPulseDbContext>();
            deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({chunk}) FROM PriceTicks WHERE TimestampUtc < {cutoff}", ct);
            total += deleted;
        }
        while (deleted == chunk);

        if (total > 0)
        {
            logger.LogInformation(
                "Retention sweep deleted {Count} ticks older than {Cutoff:u}.", total, cutoff);
        }
    }
}
```

In `AddInfrastructure`, directly under the `AddHostedService<TickPersistenceService>()` line:

```csharp
services.AddHostedService<TickRetentionService>();
```

- [ ] **Step 4: Run to verify green**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~TickRetentionTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(history): chunked retention sweep keeps PriceTicks bounded"
```

---

### Task 8: Candle read path — IPriceHistoryReader + Dapper implementation

**Files:**
- Create: `src/MarketPulse.Application/Abstractions/IPriceHistoryReader.cs`
- Create: `src/MarketPulse.Infrastructure/History/DapperPriceHistoryReader.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs` (`AddPersistence`)
- Test: `tests/MarketPulse.IntegrationTests/CandleQueryTests.cs`

**Interfaces:**
- Produces: `record Candle(DateTimeOffset BucketStartUtc, decimal Open, decimal High, decimal Low, decimal Close)` and `IPriceHistoryReader { Task<IReadOnlyList<Candle>> GetCandlesAsync(string ticker, int intervalSeconds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct); }` — Task 9's handler consumes; Task 11 adds `GetSparklinesAsync` to this same interface.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.IntegrationTests/CandleQueryTests.cs`:

```csharp
using MarketPulse.Infrastructure.History;
using MarketPulse.Infrastructure.Persistence;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class CandleQueryTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
    private DapperPriceHistoryReader _reader = null!;

    public async Task InitializeAsync()
    {
        _reader = new DapperPriceHistoryReader(new SqlConnectionFactory(fixture.ConnectionString));

        await using var db = fixture.CreateContext();
        if (await Task.FromResult(db.PriceTicks.Any(t => t.Ticker == "ZCND")))
        {
            return; // seeded by an earlier test in this class's lifetime
        }

        db.PriceTicks.AddRange(
            // bucket 10:00 — open 10, high 12, low 9, close 11
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(5), Price = 10m },
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(20), Price = 12m },
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(40), Price = 9m },
            new() { Ticker = "ZCND", TimestampUtc = T0.AddSeconds(59), Price = 11m },
            // exactly on the boundary — belongs to bucket 10:01, alone: o=h=l=c
            new() { Ticker = "ZCND", TimestampUtc = T0.AddMinutes(1), Price = 13m });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task One_minute_candles_have_correct_ohlc_per_bucket()
    {
        var candles = await _reader.GetCandlesAsync(
            "ZCND", 60, T0, T0.AddMinutes(2), CancellationToken.None);

        Assert.Equal(2, candles.Count);

        Assert.Equal(T0, candles[0].BucketStartUtc);
        Assert.Equal(10m, candles[0].Open);
        Assert.Equal(12m, candles[0].High);
        Assert.Equal(9m, candles[0].Low);
        Assert.Equal(11m, candles[0].Close);

        Assert.Equal(T0.AddMinutes(1), candles[1].BucketStartUtc);
        Assert.Equal(13m, candles[1].Open);
        Assert.Equal(13m, candles[1].Close); // single tick: o=h=l=c
    }

    [Fact]
    public async Task The_to_bound_is_exclusive()
    {
        var candles = await _reader.GetCandlesAsync(
            "ZCND", 60, T0, T0.AddMinutes(1), CancellationToken.None);

        var only = Assert.Single(candles);
        Assert.Equal(T0, only.BucketStartUtc); // the 10:01:00 tick is outside [from, to)
    }

    [Fact]
    public async Task A_range_with_no_ticks_returns_empty()
    {
        var candles = await _reader.GetCandlesAsync(
            "ZCND", 60, T0.AddDays(1), T0.AddDays(1).AddMinutes(5), CancellationToken.None);

        Assert.Empty(candles);
    }
}
```

- [ ] **Step 2: Run to verify they fail** (compile error)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~CandleQueryTests"
```

- [ ] **Step 3: Implement**

`src/MarketPulse.Application/Abstractions/IPriceHistoryReader.cs`:

```csharp
namespace MarketPulse.Application.Abstractions;

public record Candle(DateTimeOffset BucketStartUtc, decimal Open, decimal High, decimal Low, decimal Close);

public interface IPriceHistoryReader
{
    /// <summary>OHLC per bucket over [fromUtc, toUtc), oldest first. Buckets with no ticks are omitted.</summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string ticker, int intervalSeconds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
```

`src/MarketPulse.Infrastructure/History/DapperPriceHistoryReader.cs`:

```csharp
using Dapper;
using MarketPulse.Application.Abstractions;

namespace MarketPulse.Infrastructure.History;

/// <summary>
/// The codebase's first Dapper code — deliberately: these are read-heavy, shape-fixed
/// queries where materialising tracked entities buys nothing. Buckets are integer
/// arithmetic on seconds-since-2000, which the clustered (Ticker, TimestampUtc) index
/// feeds in order; DATEDIFF's int return holds until 2068.
/// </summary>
public sealed class DapperPriceHistoryReader(ISqlConnectionFactory connections) : IPriceHistoryReader
{
    private const string CandleSql = """
        WITH ranged AS (
            SELECT TimestampUtc, Price,
                   DATEDIFF(SECOND, '2000-01-01', TimestampUtc) / @IntervalSeconds AS Bucket
            FROM PriceTicks
            WHERE Ticker = @Ticker AND TimestampUtc >= @FromUtc AND TimestampUtc < @ToUtc
        ),
        ordered AS (
            SELECT Bucket, Price,
                   ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY TimestampUtc ASC)  AS RnOpen,
                   ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY TimestampUtc DESC) AS RnClose
            FROM ranged
        )
        SELECT
            TODATETIMEOFFSET(DATEADD(SECOND, Bucket * @IntervalSeconds, '2000-01-01'), 0) AS BucketStartUtc,
            MAX(CASE WHEN RnOpen  = 1 THEN Price END) AS [Open],
            MAX(Price)                                AS [High],
            MIN(Price)                                AS [Low],
            MAX(CASE WHEN RnClose = 1 THEN Price END) AS [Close]
        FROM ordered
        GROUP BY Bucket
        ORDER BY Bucket;
        """;

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string ticker, int intervalSeconds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        var rows = await connection.QueryAsync<Candle>(new CommandDefinition(
            CandleSql,
            new { Ticker = ticker, IntervalSeconds = intervalSeconds, FromUtc = fromUtc, ToUtc = toUtc },
            cancellationToken: ct));
        return rows.ToList();
    }
}
```

In `AddPersistence`, beside the repository registrations:

```csharp
services.AddScoped<IPriceHistoryReader, DapperPriceHistoryReader>();
```

(add `using MarketPulse.Infrastructure.History;` if not already present from Task 6.)

- [ ] **Step 4: Run to verify green**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~CandleQueryTests"
```

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(history): Dapper candle query — OHLC aggregation on read"
```

---

### Task 9: Candles endpoint — query, validator, 404, controller

**Files:**
- Create: `src/MarketPulse.Domain/Exceptions/PriceHistoryExceptions.cs`
- Create: `src/MarketPulse.Application/PriceHistory/GetCandlesQuery.cs`
- Create: `src/MarketPulse.Api/Controllers/PricesController.cs`
- Test: `tests/MarketPulse.IntegrationTests/CandleApiTests.cs`

**Interfaces:**
- Consumes: `IPriceHistoryReader.GetCandlesAsync` (Task 8), `IWatchlistRepository.TickerExistsAsync(string code, CancellationToken ct)` (existing), `HistoryOptions.MaxCandleBuckets` (Task 2).
- Produces: `GetCandlesQuery(string Ticker, string Interval, string From, string To) : IRequest<CandlesDto>`, `CandlesDto(string Ticker, string Interval, IReadOnlyList<CandleDto> Candles)`, `CandleDto(DateTimeOffset T, decimal O, decimal H, decimal L, decimal C)`, `CandleIntervals.Seconds` map, and route `GET /api/v1/prices/{ticker}/candles`. Task 10 adds the sparklines action to this controller.

- [ ] **Step 1: Write the failing contract tests**

`tests/MarketPulse.IntegrationTests/CandleApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using MarketPulse.Application.PriceHistory;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class CandleApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = TestFactory.Create(fixture);
        _client = await AuthenticatedClient.RegisterAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static string Url(string ticker, string interval = "1m",
        string from = "2026-08-06T00:00:00Z", string to = "2026-08-06T01:00:00Z") =>
        $"/api/v1/prices/{ticker}/candles?interval={interval}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}";

    [Fact]
    public async Task Known_ticker_with_no_data_returns_200_and_empty_candles()
    {
        var response = await _client.GetAsync(Url("IVV", from: "2020-01-01T00:00:00Z", to: "2020-01-01T01:00:00Z"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CandlesDto>();
        Assert.NotNull(body);
        Assert.Equal("IVV", body!.Ticker);
        Assert.Empty(body.Candles);
    }

    [Fact]
    public async Task Unknown_ticker_returns_404_unknown_ticker()
    {
        var response = await _client.GetAsync(Url("ZZZZ"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("unknown-ticker", body!["title"].ToString());
    }

    [Fact]
    public async Task Unlisted_interval_returns_400_invalid_interval()
    {
        var response = await _client.GetAsync(Url("IVV", interval: "42s"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-interval", body!["title"].ToString());
    }

    [Fact]
    public async Task Inverted_or_unparseable_range_returns_400_invalid_range()
    {
        var inverted = await _client.GetAsync(
            Url("IVV", from: "2026-08-06T02:00:00Z", to: "2026-08-06T01:00:00Z"));
        var garbage = await _client.GetAsync(Url("IVV", from: "not-a-date"));

        Assert.Equal(HttpStatusCode.BadRequest, inverted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        var body = await inverted.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("invalid-range", body!["title"].ToString());
    }

    [Fact]
    public async Task A_range_exceeding_the_bucket_cap_returns_400_range_too_large()
    {
        // 1000-bucket cap at 1m: 30 days is 43,200 buckets
        var response = await _client.GetAsync(
            Url("IVV", from: "2026-07-01T00:00:00Z", to: "2026-08-01T00:00:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("range-too-large", body!["title"].ToString());
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(Url("IVV"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 2: Run to verify they fail** (compile error: `CandlesDto` missing)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~CandleApiTests"
```

- [ ] **Step 3: Implement the domain exception**

`src/MarketPulse.Domain/Exceptions/PriceHistoryExceptions.cs`:

```csharp
namespace MarketPulse.Domain.Exceptions;

/// <summary>
/// 404, not the alerts path's 400: there the unknown ticker arrives in a request body and
/// is a validation failure; here it names the resource in the path, and a resource that
/// does not exist is a 404. Same slug both ways — clients branch on the code, and the code
/// means the same thing.
/// </summary>
public sealed class TickerNotFoundException(string ticker)
    : DomainException($"Ticker '{ticker}' is not a known instrument.")
{
    public override string ErrorCode => "unknown-ticker";
    public override int StatusCode => 404;
}
```

- [ ] **Step 4: Implement query, validator, handler**

`src/MarketPulse.Application/PriceHistory/GetCandlesQuery.cs`:

```csharp
using System.Globalization;
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.PriceHistory;

public record CandleDto(DateTimeOffset T, decimal O, decimal H, decimal L, decimal C);

public record CandlesDto(string Ticker, string Interval, IReadOnlyList<CandleDto> Candles);

/// <summary>
/// From/To arrive as raw strings so the validator owns every failure mode with a slugged
/// 400 — model binding never gets the chance to reject with an unslugged one.
/// </summary>
public record GetCandlesQuery(string Ticker, string Interval, string From, string To)
    : IRequest<CandlesDto>;

public static class CandleIntervals
{
    public static readonly IReadOnlyDictionary<string, int> Seconds =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["1m"] = 60, ["5m"] = 300, ["1h"] = 3600, ["1d"] = 86_400
        };
}

public sealed class GetCandlesValidator : AbstractValidator<GetCandlesQuery>
{
    public GetCandlesValidator(IOptions<HistoryOptions> options)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithErrorCode("invalid-ticker")
            .MaximumLength(8).WithErrorCode("invalid-ticker");

        RuleFor(x => x.Interval)
            .Must(CandleIntervals.Seconds.ContainsKey)
            .WithErrorCode("invalid-interval")
            .WithMessage("Interval must be one of: 1m, 5m, 1h, 1d.");

        RuleFor(x => x)
            .Must(q => TryParseRange(q, out _, out _))
            .WithErrorCode("invalid-range")
            .WithMessage("From and To must be valid timestamps with From earlier than To.");

        RuleFor(x => x)
            .Must(q => !TryParseRange(q, out var from, out var to)
                       || !CandleIntervals.Seconds.TryGetValue(q.Interval, out var seconds)
                       || (to - from).TotalSeconds / seconds <= options.Value.MaxCandleBuckets)
            .WithErrorCode("range-too-large")
            .WithMessage($"Range must not exceed {options.Value.MaxCandleBuckets} buckets.");
    }

    internal static bool TryParseRange(GetCandlesQuery q, out DateTimeOffset from, out DateTimeOffset to)
    {
        to = default;
        return DateTimeOffset.TryParse(q.From, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out from)
            && DateTimeOffset.TryParse(q.To, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out to)
            && from < to;
    }
}

public sealed class GetCandlesHandler(IPriceHistoryReader reader, IWatchlistRepository watchlists)
    : IRequestHandler<GetCandlesQuery, CandlesDto>
{
    public async Task<CandlesDto> Handle(GetCandlesQuery request, CancellationToken ct)
    {
        var ticker = request.Ticker.ToUpperInvariant();

        if (!await watchlists.TickerExistsAsync(ticker, ct))
        {
            throw new TickerNotFoundException(ticker);
        }

        // The validator has already proven this parses; parse again rather than smuggle
        // state between pipeline stages.
        GetCandlesValidator.TryParseRange(request, out var from, out var to);

        var candles = await reader.GetCandlesAsync(
            ticker, CandleIntervals.Seconds[request.Interval], from, to, ct);

        return new CandlesDto(
            ticker,
            request.Interval,
            candles.Select(c => new CandleDto(c.BucketStartUtc, c.Open, c.High, c.Low, c.Close)).ToList());
    }
}
```

- [ ] **Step 5: Implement the controller**

`src/MarketPulse.Api/Controllers/PricesController.cs`:

```csharp
using MarketPulse.Application.PriceHistory;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/prices")]
public sealed class PricesController(ISender sender) : ControllerBase
{
    [HttpGet("{ticker}/candles")]
    public async Task<ActionResult<CandlesDto>> GetCandles(
        string ticker,
        [FromQuery] string interval = "1m",
        [FromQuery] string from = "",
        [FromQuery] string to = "",
        CancellationToken ct = default) =>
        Ok(await sender.Send(new GetCandlesQuery(ticker, interval, from, to), ct));
}
```

- [ ] **Step 6: Run to verify green** (validators are discovered by `AddApplication()`'s assembly scan — no registration needed)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~CandleApiTests"
```

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(history): GET /api/v1/prices/{ticker}/candles with slugged error contract"
```

---

### Task 10: Sparklines endpoint — the deliberate N+1, plus cross-user isolation

**Files:**
- Create: `src/MarketPulse.Application/PriceHistory/GetSparklinesQuery.cs`
- Modify: `src/MarketPulse.Api/Controllers/PricesController.cs`
- Test: `tests/MarketPulse.IntegrationTests/SparklineApiTests.cs`

**Interfaces:**
- Consumes: `IPriceHistoryReader.GetCandlesAsync` (Task 8), `IWatchlistRepository.GetForUserAsync(Guid userId, CancellationToken ct)` (existing), `ICurrentUser.UserId` (existing), `HistoryOptions.SparklineWindowMinutes` (Task 2).
- Produces: `GetSparklinesQuery : IRequest<SparklinesDto>`, `SparklinesDto(IReadOnlyDictionary<string, IReadOnlyList<decimal>> Sparklines)`, route `GET /api/v1/prices/sparklines`. Task 11 rewrites only the handler's body.

- [ ] **Step 1: Write the failing tests**

`tests/MarketPulse.IntegrationTests/SparklineApiTests.cs`:

```csharp
using System.Net.Http.Json;
using MarketPulse.Application.PriceHistory;
using MarketPulse.Application.Watchlists;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class SparklineApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        _factory = TestFactory.Create(fixture);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Returns_recent_closes_for_exactly_the_callers_watchlist()
    {
        var client = await AuthenticatedClient.RegisterAsync(_factory);
        (await client.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV")))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("NDQ")))
            .EnsureSuccessStatusCode();

        // Recent history for one of the two — the other legitimately has no points yet.
        // (Rows may also exist from the Fake feed; this test only asserts key membership
        // and that IVV's seeded closes appear in order.)
        var now = DateTimeOffset.UtcNow;
        await using (var db = fixture.CreateContext())
        {
            db.PriceTicks.AddRange(
                new() { Ticker = "IVV", TimestampUtc = now.AddMinutes(-3), Price = 101m },
                new() { Ticker = "IVV", TimestampUtc = now.AddMinutes(-2), Price = 102m });
            await db.SaveChangesAsync();
        }

        var body = await client.GetFromJsonAsync<SparklinesDto>("/api/v1/prices/sparklines");

        Assert.NotNull(body);
        Assert.Equal(2, body!.Sparklines.Count);
        Assert.Contains("IVV", body.Sparklines.Keys);
        Assert.Contains("NDQ", body.Sparklines.Keys);
        Assert.Contains(101m, body.Sparklines["IVV"]);
        Assert.Contains(102m, body.Sparklines["IVV"]);
    }

    [Fact]
    public async Task An_empty_watchlist_returns_an_empty_object()
    {
        var client = await AuthenticatedClient.RegisterAsync(_factory);

        var body = await client.GetFromJsonAsync<SparklinesDto>("/api/v1/prices/sparklines");

        Assert.NotNull(body);
        Assert.Empty(body!.Sparklines);
    }

    [Fact]
    public async Task Bobs_sparklines_never_include_alices_tickers()
    {
        var alice = await AuthenticatedClient.RegisterAsync(_factory);
        (await alice.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("VAS")))
            .EnsureSuccessStatusCode();
        var bob = await AuthenticatedClient.RegisterAsync(_factory);

        var body = await bob.GetFromJsonAsync<SparklinesDto>("/api/v1/prices/sparklines");

        Assert.NotNull(body);
        Assert.DoesNotContain("VAS", body!.Sparklines.Keys);
    }
}
```

(If `VAS` is not in `SeedData.cs`'s 25 tickers, substitute any seeded code — verify with `grep -o '"[A-Z]*"' src/MarketPulse.Infrastructure/Persistence/SeedData.cs | sort -u`.)

- [ ] **Step 2: Run to verify they fail** (compile error: `SparklinesDto` missing)

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~SparklineApiTests"
```

- [ ] **Step 3: Implement — with the N+1, on purpose**

`src/MarketPulse.Application/PriceHistory/GetSparklinesQuery.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MediatR;
using Microsoft.Extensions.Options;

namespace MarketPulse.Application.PriceHistory;

public record SparklinesDto(IReadOnlyDictionary<string, IReadOnlyList<decimal>> Sparklines);

public record GetSparklinesQuery : IRequest<SparklinesDto>;

public sealed class GetSparklinesHandler(
    IPriceHistoryReader reader,
    IWatchlistRepository watchlists,
    ICurrentUser user,
    IOptions<HistoryOptions> options,
    TimeProvider timeProvider) : IRequestHandler<GetSparklinesQuery, SparklinesDto>
{
    public async Task<SparklinesDto> Handle(GetSparklinesQuery request, CancellationToken ct)
    {
        var watchlist = await watchlists.GetForUserAsync(user.UserId, ct);
        var tickers = watchlist?.Items.Select(i => i.Ticker).OrderBy(t => t, StringComparer.Ordinal).ToList() ?? [];

        var to = timeProvider.GetUtcNow();
        var from = to.AddMinutes(-options.Value.SparklineWindowMinutes);

        // DELIBERATE N+1 (ADR-006): one candle query per watchlist ticker. Correct and
        // fully tested — which is the point: no test in this suite can see the defect.
        // Measured and replaced by a set-based read later in this same slice; both
        // measurements live in docs/sql/.
        var sparklines = new Dictionary<string, IReadOnlyList<decimal>>();
        foreach (var ticker in tickers)
        {
            var candles = await reader.GetCandlesAsync(ticker, 60, from, to, ct);
            sparklines[ticker] = candles.Select(c => c.Close).ToList();
        }

        return new SparklinesDto(sparklines);
    }
}
```

In `PricesController`, add below `GetCandles`:

```csharp
[HttpGet("sparklines")]
public async Task<ActionResult<SparklinesDto>> GetSparklines(CancellationToken ct) =>
    Ok(await sender.Send(new GetSparklinesQuery(), ct));
```

- [ ] **Step 4: Run to verify green**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~SparklineApiTests"
```

- [ ] **Step 5: Commit — the commit message says what this is**

```bash
git add -A && git commit -m "feat(history): sparklines endpoint — deliberate N+1, to be measured and fixed (ADR-006)"
```

---

### Task 11: Measure, fix, re-measure — ADR-006 and docs/sql

**Files:**
- Create: `docs/sql/seed-sparkline-history.sql`, `docs/sql/sparklines-n-plus-one.sql`, `docs/sql/sparklines-set-based.sql`, `docs/sql/README.md`, plus captured outputs `docs/sql/sparklines-before.txt`, `docs/sql/sparklines-after.txt`
- Modify: `src/MarketPulse.Application/Abstractions/IPriceHistoryReader.cs`
- Modify: `src/MarketPulse.Infrastructure/History/DapperPriceHistoryReader.cs`
- Modify: `src/MarketPulse.Application/PriceHistory/GetSparklinesQuery.cs` (handler body only)
- Create: `tests/MarketPulse.IntegrationTests/CountingSqlConnectionFactory.cs`
- Test: `tests/MarketPulse.IntegrationTests/SparklineQueryCountTests.cs`
- Create: `docs/adr/006-n-plus-one-postmortem.md`

**Interfaces:**
- Produces: `IPriceHistoryReader.GetSparklinesAsync(IReadOnlyList<string> tickers, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) → Task<IReadOnlyDictionary<string, IReadOnlyList<decimal>>>` (closes per 1-minute bucket, oldest first, only tickers with data present as keys — the handler fills in empty lists for the rest).

- [ ] **Step 1: Capture the "before" — against the dev database, with v1 still deployed**

```bash
docker compose up -d sqlserver
# password: the dev SA password from src/MarketPulse.Api/appsettings.Development.json
```

`docs/sql/seed-sparkline-history.sql`:

```sql
-- Populate a retention-window-scale PriceTicks: 20 seeded tickers × 7 days × 1/minute
-- ≈ 201,600 rows. Idempotent-ish: IGNORE_DUP_KEY absorbs replays on re-run.
SET NOCOUNT ON;
;WITH n AS (
    SELECT TOP (10080) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
),
t AS (SELECT TOP (20) Code FROM Tickers ORDER BY Code)
INSERT INTO PriceTicks (Ticker, TimestampUtc, Price)
SELECT t.Code,
       DATEADD(MINUTE, -n.i, SYSDATETIMEOFFSET()),
       50 + (ABS(CHECKSUM(t.Code)) % 100) + (n.i % 97) / 100.0
FROM t CROSS JOIN n;
PRINT CONCAT('PriceTicks rows: ', (SELECT COUNT(*) FROM PriceTicks));
```

`docs/sql/sparklines-n-plus-one.sql` — the loop shape v1 executes, one query per ticker:

```sql
-- What GetSparklinesHandler v1 does: N single-ticker candle queries. STATISTICS TIME/IO
-- per query; the plan for one representative iteration captured via SHOWPLAN_TEXT.
SET STATISTICS TIME ON;
SET STATISTICS IO ON;
DECLARE @FromUtc datetimeoffset = DATEADD(MINUTE, -60, SYSDATETIMEOFFSET());
DECLARE @ToUtc   datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @Ticker  nvarchar(8);
DECLARE tickers CURSOR FOR SELECT TOP (20) Code FROM Tickers ORDER BY Code;
OPEN tickers;
FETCH NEXT FROM tickers INTO @Ticker;
WHILE @@FETCH_STATUS = 0
BEGIN
    WITH ranged AS (
        SELECT TimestampUtc, Price,
               DATEDIFF(SECOND, '2000-01-01', TimestampUtc) / 60 AS Bucket
        FROM PriceTicks
        WHERE Ticker = @Ticker AND TimestampUtc >= @FromUtc AND TimestampUtc < @ToUtc
    )
    SELECT Bucket, MAX(CASE WHEN Rn = 1 THEN Price END) AS [Close]
    FROM (SELECT Bucket, Price,
                 ROW_NUMBER() OVER (PARTITION BY Bucket ORDER BY TimestampUtc DESC) AS Rn
          FROM ranged) x
    WHERE Rn = 1
    GROUP BY Bucket ORDER BY Bucket;
    FETCH NEXT FROM tickers INTO @Ticker;
END;
CLOSE tickers; DEALLOCATE tickers;
```

Run and capture (fill `<SA_PASSWORD>` from `appsettings.Development.json`; add `-C` if `sqlcmd` rejects the dev certificate):

```bash
sqlcmd -S localhost,1433 -U sa -P '<SA_PASSWORD>' -d MarketPulse -i docs/sql/seed-sparkline-history.sql
sqlcmd -S localhost,1433 -U sa -P '<SA_PASSWORD>' -d MarketPulse -i docs/sql/sparklines-n-plus-one.sql -o docs/sql/sparklines-before.txt
```

Also measure end-to-end: run the API (`dotnet run --project src/MarketPulse.Api`), register a user, add 20 tickers, and time 20 requests to `/api/v1/prices/sparklines`; record the numbers in `docs/sql/README.md`. (A short shell loop with `curl -w '%{time_total}\n'` and the cookie jar from login is fine — exact tooling is the executor's choice; the artifact that matters is the recorded before/after numbers alongside the SQL statistics.)

- [ ] **Step 2: Write the failing query-count pin**

`tests/MarketPulse.IntegrationTests/CountingSqlConnectionFactory.cs`:

```csharp
using System.Data.Common;
using MarketPulse.Application.Abstractions;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// Counts connections handed to the Dapper read path. Each reader method opens exactly one
/// connection and runs exactly one query on it, so connections == queries — the pin that
/// keeps ADR-006's N+1 from silently returning.
/// </summary>
public sealed class CountingSqlConnectionFactory(ISqlConnectionFactory inner) : ISqlConnectionFactory
{
    private int _opened;

    public int Opened => Volatile.Read(ref _opened);

    public void Reset() => Volatile.Write(ref _opened, 0);

    public Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _opened);
        return inner.OpenAsync(ct);
    }
}
```

`tests/MarketPulse.IntegrationTests/SparklineQueryCountTests.cs`:

```csharp
using System.Net.Http.Json;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Watchlists;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class SparklineQueryCountTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Sparklines_issue_one_history_query_regardless_of_watchlist_size()
    {
        var counter = new CountingSqlConnectionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        using var factory = TestFactory.Create(fixture).WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<ISqlConnectionFactory>(counter)));
        var client = await AuthenticatedClient.RegisterAsync(factory);

        foreach (var ticker in new[] { "IVV", "NDQ", "VAS" })
        {
            (await client.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand(ticker)))
                .EnsureSuccessStatusCode();
        }

        counter.Reset();
        (await client.GetAsync("/api/v1/prices/sparklines")).EnsureSuccessStatusCode();

        Assert.Equal(1, counter.Opened);
    }
}
```

- [ ] **Step 3: Run to verify it fails for the right reason**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~SparklineQueryCountTests"
```

Expected: FAIL with `Assert.Equal(1, ...)` seeing **3** — the N+1, caught by a test for the first time.

- [ ] **Step 4: Implement the set-based fix**

Add to `IPriceHistoryReader`:

```csharp
/// <summary>
/// Closing price per 1-minute bucket over [fromUtc, toUtc), oldest first, one set-based
/// query for all tickers. Tickers with no ticks in range are absent from the result.
/// </summary>
Task<IReadOnlyDictionary<string, IReadOnlyList<decimal>>> GetSparklinesAsync(
    IReadOnlyList<string> tickers, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
```

Add to `DapperPriceHistoryReader`:

```csharp
private const string SparklineSql = """
    WITH ranged AS (
        SELECT Ticker, TimestampUtc, Price,
               DATEDIFF(SECOND, '2000-01-01', TimestampUtc) / 60 AS Bucket
        FROM PriceTicks
        WHERE Ticker IN @Tickers AND TimestampUtc >= @FromUtc AND TimestampUtc < @ToUtc
    )
    SELECT Ticker, Bucket, Price AS [Close]
    FROM (
        SELECT Ticker, Bucket, Price,
               ROW_NUMBER() OVER (PARTITION BY Ticker, Bucket ORDER BY TimestampUtc DESC) AS Rn
        FROM ranged
    ) x
    WHERE Rn = 1
    ORDER BY Ticker, Bucket;
    """;

private sealed record SparklinePoint(string Ticker, long Bucket, decimal Close);

public async Task<IReadOnlyDictionary<string, IReadOnlyList<decimal>>> GetSparklinesAsync(
    IReadOnlyList<string> tickers, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
{
    if (tickers.Count == 0)
    {
        return new Dictionary<string, IReadOnlyList<decimal>>();
    }

    await using var connection = await connections.OpenAsync(ct);
    var points = await connection.QueryAsync<SparklinePoint>(new CommandDefinition(
        SparklineSql,
        new { Tickers = tickers, FromUtc = fromUtc, ToUtc = toUtc },
        cancellationToken: ct));

    return points
        .GroupBy(p => p.Ticker, StringComparer.Ordinal)
        .ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<decimal>)g.Select(p => p.Close).ToList(),
            StringComparer.Ordinal);
}
```

Replace the loop in `GetSparklinesHandler.Handle` (delete the `foreach` block and the dictionary above it):

```csharp
// Set-based since ADR-006: one query for every ticker, then empty lists filled in for
// tickers with no ticks in the window so the response shape stays total over the watchlist.
var withData = await reader.GetSparklinesAsync(tickers, from, to, ct);
var sparklines = tickers.ToDictionary(
    t => t,
    t => withData.GetValueOrDefault(t, []),
    StringComparer.Ordinal);
```

- [ ] **Step 5: Run to verify green — pin, behaviour, and everything previous**

```bash
dotnet test tests/MarketPulse.IntegrationTests --filter "FullyQualifiedName~SparklineQueryCountTests|FullyQualifiedName~SparklineApiTests"
```

- [ ] **Step 6: Capture the "after"**

`docs/sql/sparklines-set-based.sql` — the fixed query with the same statistics:

```sql
SET STATISTICS TIME ON;
SET STATISTICS IO ON;
DECLARE @FromUtc datetimeoffset = DATEADD(MINUTE, -60, SYSDATETIMEOFFSET());
DECLARE @ToUtc   datetimeoffset = SYSDATETIMEOFFSET();
WITH tickers AS (SELECT TOP (20) Code FROM Tickers ORDER BY Code),
ranged AS (
    SELECT p.Ticker, p.TimestampUtc, p.Price,
           DATEDIFF(SECOND, '2000-01-01', p.TimestampUtc) / 60 AS Bucket
    FROM PriceTicks p JOIN tickers t ON p.Ticker = t.Code
    WHERE p.TimestampUtc >= @FromUtc AND p.TimestampUtc < @ToUtc
)
SELECT Ticker, Bucket, Price AS [Close]
FROM (SELECT Ticker, Bucket, Price,
             ROW_NUMBER() OVER (PARTITION BY Ticker, Bucket ORDER BY TimestampUtc DESC) AS Rn
      FROM ranged) x
WHERE Rn = 1
ORDER BY Ticker, Bucket;
```

```bash
sqlcmd -S localhost,1433 -U sa -P '<SA_PASSWORD>' -d MarketPulse -i docs/sql/sparklines-set-based.sql -o docs/sql/sparklines-after.txt
```

Re-run the end-to-end timing loop from Step 1 against the rebuilt API; record in `docs/sql/README.md` alongside: what was measured, on what data volume, both sets of numbers, and one paragraph reading the plans (the before-case pays the query-setup and scan N times; the set-based case pays once — quote the actual `logical reads` totals from the two `.txt` files).

- [ ] **Step 7: Write ADR-006**

`docs/adr/006-n-plus-one-postmortem.md`, matching the existing ADR format (`Status: Accepted`, Context / Decision / Consequences), covering: the v1 loop as shipped (link the Task 10 commit), why every test stayed green while it was wrong, the measured before/after (quote the timing and logical-read totals from `docs/sql/`), the fix, and the query-count pin that keeps it fixed. Close with the transferable rule: *correctness tests cannot see query counts; pin the access pattern, not just the result.*

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "fix(history): set-based sparklines query — N+1 measured, fixed, pinned (ADR-006)"
```

---

### Task 12: Close the slice — roadmap, full gate, merge readiness

**Files:**
- Modify: `docs/ROADMAP.md`
- Verify: everything

- [ ] **Step 1: Update the roadmap in the same commit that finishes the slice (its own rule)**

In `docs/ROADMAP.md`: add slice 7a to the *Completed slices* table (date, one-paragraph delivered summary naming the persistence sink, retention, Dapper candle/sparkline reads, ADR-006 + `docs/sql/`); update the *Phase status* row for phase 2 (Partial — 7b chart UI outstanding); rewrite the *Remaining slices* entry for slice 7 to be 7b only; move the `docs/adr/006-n-plus-one-postmortem.md` and `docs/sql/` rows out of the deferred-claims register.

- [ ] **Step 2: Run the full local gate**

```bash
dotnet build -c Release          # must end: 0 Warning(s)
dotnet test                      # all unit + integration projects
pnpm -r typecheck && pnpm -r test  # frontend untouched — prove it stayed green
```

- [ ] **Step 3: Manual fake-mode check (spec's done criteria)**

```bash
docker compose up -d sqlserver
dotnet run --project src/MarketPulse.Api
# ≥5 minutes later, in a second terminal:
#   - PriceTicks row count is growing (sqlcmd SELECT COUNT(*))
#   - authenticated GET /api/v1/prices/IVV/candles?interval=1m&from=<10 min ago>&to=<now> returns plausible OHLC
#   - with History:RetentionDays temporarily set low via env var, a sweep deletes old rows
```

Record the outcome in the task report (rows observed, one sample candle response).

- [ ] **Step 4: Commit and stop**

```bash
git add -A && git commit -m "docs: close slice 7a — roadmap status, phase 2 partial pending 7b"
```

Do **not** merge to `test` in this task. Merging is the finishing-a-development-branch skill's job, after review: full-branch review → merge `feature/slice-7a-price-history` → `test` → local verification on `test` (repo promotion flow).

---

## Self-review notes (already applied)

- **Spec coverage:** persistence sink (T4–6), schema + dedupe (T3), retention (T7), options (T2), Dapper + candles (T8), API + error surface (T9), sparklines + N+1 + pin + ADR-006 + docs/sql (T10–11), cross-user (T10), done criteria + roadmap (T12). Spec's "Testing" list maps 1:1 onto task test files.
- **Deviation from spec, recorded:** `TimestampUtc` is `datetimeoffset` (repo convention), not the spec sketch's `datetime2`; flush semantics implement "5s or 500 rows" as timer-driven flushes chunked at 500 — the chunk cap is what bounds a single INSERT, which is the property that matters (parameter limit).
- **Type consistency:** `ISqlConnectionFactory.OpenAsync` (T1) is what `DapperPriceHistoryReader` (T8) and `CountingSqlConnectionFactory` (T11) implement; `Candle.BucketStartUtc` (T8) feeds `CandleDto.T` (T9); `GetSparklinesAsync` signature in T11's interface matches its implementation and the handler call.
