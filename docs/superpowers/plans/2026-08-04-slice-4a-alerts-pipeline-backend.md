# Slice 4a — Alerts Pipeline Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the server-side alerts pipeline end to end — alert-rule CRUD, price ticks onto RabbitMQ, an extracted worker that evaluates rules, a transactional outbox, an idempotent consumer, and per-user SignalR notification delivery — proven by integration tests against real SQL Server and real RabbitMQ.

**Architecture:** The API publishes ticks to a topic exchange alongside its existing SignalR broadcast. A new `MarketPulse.Alerts` worker consumes them, evaluates rules, and in one transaction marks the rule triggered and writes an outbox row. A dispatcher relays outbox rows to a second exchange with publisher confirms. The API consumes that queue, dedupes on message id at the database, persists a notification, and pushes it to that user alone. Worker and API share one SQL Server database — the compute is extracted, the data is not.

**Tech Stack:** .NET 10, EF Core 10 (SQL Server), MediatR, FluentValidation, RabbitMQ.Client v7 (async API), SignalR, xUnit, NSubstitute, Testcontainers (MsSql + RabbitMq).

**Spec:** [2026-08-04-alerts-pipeline-backend-design.md](../specs/2026-08-04-alerts-pipeline-backend-design.md) — read it before starting. Every "why" is there; this document is the "how".

## Global Constraints

- **Clean Architecture layering is enforced by tests.** `Domain` references nothing but the BCL. `Application` must not reference `Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`, `Microsoft.Data`, `Microsoft.Extensions.Identity`, or `RabbitMQ`. All five are asserted in `tests/MarketPulse.UnitTests/Architecture/DependencyRuleTests.cs`. Messaging interfaces are declared in `MarketPulse.Application/Abstractions/`, implemented in `MarketPulse.Infrastructure/Messaging/`.
- **`MarketPulse.Alerts` must not reference `Microsoft.AspNetCore`.** A new assertion in the same test file enforces it.
- **Central package management.** Versions go in `Directory.Packages.props`; `.csproj` files carry bare `<PackageReference Include="…" />` with no `Version` attribute.
- **All new API endpoints** are `[Authorize]`, routed under `api/v1/`, scoped by `ICurrentUser.UserId`, and return RFC 7807 ProblemDetails on failure via the existing `ExceptionHandlingMiddleware`. Domain failures throw a `DomainException` subclass carrying `ErrorCode` and `StatusCode`.
- **A resource owned by another user returns `404`, never `403`.**
- **Entities** have private parameterless constructors, `private set` properties, and static factory methods. See `RefreshToken` for the house pattern.
- **Test naming** is a sentence with underscores, e.g. `A_rule_below_its_threshold_does_not_trigger`.
- **Commits** are conventional-commit style. **Do not add a `Co-Authored-By` trailer** — this repo omits it.
- **Run the full backend suite** (`dotnet test`) before each commit, not just the new test.
- **Exact values from the spec:** 20 alert rules per user · notification page size default 50, max 100 · tick queue `x-message-ttl` 5000ms, `x-max-length` 1000, `x-overflow` `drop-head` · consumer prefetch 100 · dispatcher poll interval 500ms.

## File Structure

| File | Responsibility |
|---|---|
| `src/MarketPulse.Domain/Entities/AlertRule.cs` | The rule aggregate: `Evaluate`, `MarkTriggered`, `Rearm`, `MaxPerUser` |
| `src/MarketPulse.Domain/Entities/Notification.cs` | A delivered alert, snapshotted off the rule |
| `src/MarketPulse.Domain/Entities/OutboxMessage.cs` | One pending or dispatched integration event |
| `src/MarketPulse.Domain/Exceptions/AlertExceptions.cs` | The six alert-specific `DomainException` subclasses |
| `src/MarketPulse.Application/Abstractions/IAlertRuleRepository.cs` | Rule reads, writes, the per-user count, and `SaveChangesAsync` |
| `src/MarketPulse.Application/Abstractions/INotificationRepository.cs` | Notification reads and the deduped insert |
| `src/MarketPulse.Application/Abstractions/IOutbox.cs` | Enqueue an event onto the same unit of work |
| `src/MarketPulse.Application/Abstractions/IEventPublisher.cs` | Publish one confirmed message to the broker |
| `src/MarketPulse.Application/Abstractions/ITickSink.cs` | One destination for a price tick |
| `src/MarketPulse.Application/Configuration/RabbitMqOptions.cs` | Broker connection and topology names |
| `src/MarketPulse.Application/Alerts/*.cs` | Rule commands, queries, validators, handlers, DTOs |
| `src/MarketPulse.Application/Notifications/*.cs` | Notification query and mark-read command |
| `src/MarketPulse.Infrastructure/Messaging/RabbitMqConnection.cs` | Connection lifetime, backoff, automatic recovery |
| `src/MarketPulse.Infrastructure/Messaging/RabbitMqTopology.cs` | Exchange, queue, binding and DLX declarations |
| `src/MarketPulse.Infrastructure/Messaging/RabbitMqEventPublisher.cs` | `IEventPublisher` over a confirming channel |
| `src/MarketPulse.Infrastructure/Messaging/RabbitMqTickSink.cs` | `ITickSink` publishing to the prices exchange |
| `src/MarketPulse.Infrastructure/Messaging/Contracts/*.cs` | `PriceTickMessage`, `AlertTriggeredMessage` |
| `src/MarketPulse.Infrastructure/Persistence/AlertRuleRepository.cs`, `NotificationRepository.cs`, `Outbox.cs` | EF implementations over the shared `MarketPulseDbContext` |
| `src/MarketPulse.Api/RealTime/SignalRTickSink.cs` | The existing broadcast, behind `ITickSink` |
| `src/MarketPulse.Api/RealTime/TickBroadcaster.cs` | *(modified)* single channel reader, fans out over sinks |
| `src/MarketPulse.Api/Hubs/NotificationHub.cs` | Per-user notification transport |
| `src/MarketPulse.Api/Messaging/AlertTriggeredConsumer.cs` | Idempotent consumer, DLQ policy, SignalR push |
| `src/MarketPulse.Api/Controllers/AlertsController.cs`, `NotificationsController.cs` | HTTP surface |
| `src/MarketPulse.Alerts/*` | New Worker: `Program.cs`, `PriceConsumer.cs`, `AlertEvaluator.cs`, `OutboxDispatcher.cs` |

Ten tasks. Each ends green and committed.

---

### Task 1: The `AlertRule` aggregate

Pure domain. No database, no broker. This is the engine the README's TDD claim points at, so the test is written first and in full.

**Files:**
- Create: `src/MarketPulse.Domain/Entities/AlertRule.cs`
- Create: `src/MarketPulse.Domain/Exceptions/AlertExceptions.cs`
- Test: `tests/MarketPulse.UnitTests/Domain/AlertRuleTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `AlertRule` with `static AlertRule Create(Guid userId, string ticker, AlertDirection direction, decimal threshold, DateTimeOffset now)`, `bool Evaluate(decimal price)`, `void MarkTriggered(decimal price, DateTimeOffset now)`, `void Rearm()`, `const int MaxPerUser = 20`; enums `AlertDirection { Above, Below }` and `AlertRuleStatus { Active, Triggered }`; exceptions `InvalidThresholdException`, `AlertRuleLimitException`, `DuplicateAlertRuleException`, `AlertRuleNotFoundException`, `AlertNotTriggeredException`, `UnknownTickerException`.

- [ ] **Step 1: Write the failing tests**

Create `tests/MarketPulse.UnitTests/Domain/AlertRuleTests.cs`:

```csharp
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.UnitTests.Domain;

public class AlertRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AlertRule Rule(AlertDirection direction, decimal threshold) =>
        AlertRule.Create(Guid.NewGuid(), "IVV", direction, threshold, Now);

    // The truth table. Evaluate is a pure function of (rule, price) with no prior-tick
    // memory, which is the whole reason it can be tested like this.
    [Theory]
    [InlineData(AlertDirection.Above, 50.00, 50.01, true)]
    [InlineData(AlertDirection.Above, 50.00, 50.00, true)]   // boundary is inclusive
    [InlineData(AlertDirection.Above, 50.00, 49.99, false)]
    [InlineData(AlertDirection.Below, 50.00, 49.99, true)]
    [InlineData(AlertDirection.Below, 50.00, 50.00, true)]   // boundary is inclusive
    [InlineData(AlertDirection.Below, 50.00, 50.01, false)]
    public void Evaluate_answers_the_truth_table(
        AlertDirection direction, decimal threshold, decimal price, bool expected)
    {
        Assert.Equal(expected, Rule(direction, threshold).Evaluate(price));
    }

    [Fact]
    public void An_already_triggered_rule_never_evaluates_true_again()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(55m, Now);

        // One-shot semantics: the price is still well above the threshold, and that is
        // exactly the case that must not fire a second time.
        Assert.False(rule.Evaluate(55m));
    }

    [Fact]
    public void A_new_rule_is_active_and_carries_no_trigger_details()
    {
        var rule = Rule(AlertDirection.Above, 50m);

        Assert.Equal(AlertRuleStatus.Active, rule.Status);
        Assert.Null(rule.TriggeredUtc);
        Assert.Null(rule.TriggeredPrice);
        Assert.Equal(Now, rule.CreatedUtc);
    }

    [Fact]
    public void MarkTriggered_records_the_price_and_the_time()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(51.25m, Now);

        Assert.Equal(AlertRuleStatus.Triggered, rule.Status);
        Assert.Equal(51.25m, rule.TriggeredPrice);
        Assert.Equal(Now, rule.TriggeredUtc);
    }

    [Fact]
    public void MarkTriggered_on_an_already_triggered_rule_is_rejected()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(51m, Now);

        Assert.Throws<AlertNotActiveException>(() => rule.MarkTriggered(52m, Now));
    }

    [Fact]
    public void Rearm_returns_a_triggered_rule_to_active_and_clears_the_details()
    {
        var rule = Rule(AlertDirection.Above, 50m);
        rule.MarkTriggered(51m, Now);
        rule.Rearm();

        Assert.Equal(AlertRuleStatus.Active, rule.Status);
        Assert.Null(rule.TriggeredUtc);
        Assert.Null(rule.TriggeredPrice);
        Assert.True(rule.Evaluate(51m));
    }

    [Fact]
    public void Rearm_on_an_active_rule_is_rejected()
    {
        Assert.Throws<AlertNotTriggeredException>(() => Rule(AlertDirection.Above, 50m).Rearm());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_of_zero_or_less_is_rejected(decimal threshold)
    {
        Assert.Throws<InvalidThresholdException>(() => Rule(AlertDirection.Above, threshold));
    }

    [Fact]
    public void The_ticker_is_normalised_the_way_watchlist_items_are()
    {
        var rule = AlertRule.Create(Guid.NewGuid(), "  ivv ", AlertDirection.Above, 50m, Now);

        Assert.Equal("IVV", rule.Ticker);
    }

    [Fact]
    public void The_per_user_limit_is_twenty()
    {
        // Enforced in the create handler, not here — an AlertRule cannot see its siblings.
        // Asserted so the number cannot drift away from the spec unnoticed.
        Assert.Equal(20, AlertRule.MaxPerUser);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~AlertRuleTests`
Expected: compile failure — `AlertRule`, `AlertDirection`, `AlertRuleStatus` and the exception types do not exist.

- [ ] **Step 3: Write the exceptions**

Create `src/MarketPulse.Domain/Exceptions/AlertExceptions.cs`:

```csharp
namespace MarketPulse.Domain.Exceptions;

public sealed class InvalidThresholdException()
    : DomainException("An alert threshold must be greater than zero.")
{
    public override string ErrorCode => "invalid-threshold";
    public override int StatusCode => 400;
}

public sealed class UnknownTickerException(string ticker)
    : DomainException($"'{ticker}' is not a known ticker.")
{
    public override string ErrorCode => "unknown-ticker";
    public override int StatusCode => 400;
}

public sealed class AlertRuleLimitException(int max)
    : DomainException($"A user may hold at most {max} alert rules.")
{
    public override string ErrorCode => "alert-limit-reached";
}

public sealed class DuplicateAlertRuleException(string ticker)
    : DomainException($"An identical active alert already exists for '{ticker}'.")
{
    public override string ErrorCode => "duplicate-alert-rule";
}

/// <summary>
/// Also raised when the rule belongs to another user. Answering 404 rather than 403 keeps
/// the endpoint from confirming that someone else's rule id exists.
/// </summary>
public sealed class AlertRuleNotFoundException()
    : DomainException("No such alert rule.")
{
    public override string ErrorCode => "alert-rule-not-found";
    public override int StatusCode => 404;
}

public sealed class AlertNotTriggeredException()
    : DomainException("Only a triggered alert can be re-armed.")
{
    public override string ErrorCode => "alert-not-triggered";
}

public sealed class AlertNotActiveException()
    : DomainException("Only an active alert can be triggered.")
{
    public override string ErrorCode => "alert-not-active";
}

public sealed class NotificationNotFoundException()
    : DomainException("No such notification.")
{
    public override string ErrorCode => "notification-not-found";
    public override int StatusCode => 404;
}
```

- [ ] **Step 4: Write the aggregate**

Create `src/MarketPulse.Domain/Entities/AlertRule.cs`:

```csharp
using MarketPulse.Domain.Exceptions;

namespace MarketPulse.Domain.Entities;

public enum AlertDirection { Above, Below }

public enum AlertRuleStatus { Active, Triggered }

/// <summary>
/// One threshold an investor asked to be told about. One-shot: it fires once and stops,
/// until the owner re-arms it. See the slice 4a spec for why crossing detection and
/// cooldown windows were both rejected.
/// </summary>
public sealed class AlertRule
{
    /// <summary>
    /// Mirrors <see cref="Watchlist.MaxItems"/>. Enforced in the create handler against a
    /// count query, because a standalone rule cannot see its siblings.
    /// </summary>
    public const int MaxPerUser = 20;

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public AlertDirection Direction { get; private set; }
    public decimal Threshold { get; private set; }
    public AlertRuleStatus Status { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset? TriggeredUtc { get; private set; }
    public decimal? TriggeredPrice { get; private set; }

    /// <summary>
    /// SQL Server rowversion. Two worker instances compete on one queue, so two ticks can
    /// race the same rule; the loser's UPDATE matches no row and is discarded. This is what
    /// makes ADR-001's "scales independently" true rather than aspirational.
    /// </summary>
    public byte[] RowVersion { get; private set; } = [];

    private AlertRule() { }

    public static AlertRule Create(
        Guid userId,
        string ticker,
        AlertDirection direction,
        decimal threshold,
        DateTimeOffset now)
    {
        if (threshold <= 0)
        {
            throw new InvalidThresholdException();
        }

        return new AlertRule
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Ticker = ticker.Trim().ToUpperInvariant(),
            Direction = direction,
            Threshold = threshold,
            Status = AlertRuleStatus.Active,
            CreatedUtc = now
        };
    }

    public bool Evaluate(decimal price) =>
        Status == AlertRuleStatus.Active &&
        (Direction is AlertDirection.Above ? price >= Threshold : price <= Threshold);

    public void MarkTriggered(decimal price, DateTimeOffset now)
    {
        if (Status != AlertRuleStatus.Active)
        {
            throw new AlertNotActiveException();
        }

        Status = AlertRuleStatus.Triggered;
        TriggeredPrice = price;
        TriggeredUtc = now;
    }

    public void Rearm()
    {
        if (Status != AlertRuleStatus.Triggered)
        {
            throw new AlertNotTriggeredException();
        }

        Status = AlertRuleStatus.Active;
        TriggeredPrice = null;
        TriggeredUtc = null;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~AlertRuleTests`
Expected: PASS, 16 tests.

- [ ] **Step 6: Run the whole suite and commit**

Run: `dotnet test`
Expected: PASS — nothing else references these types yet.

```bash
git add src/MarketPulse.Domain tests/MarketPulse.UnitTests/Domain/AlertRuleTests.cs
git commit -m "feat(domain): add the AlertRule aggregate with one-shot trigger semantics"
```

---

### Task 2: `Notification`, `OutboxMessage`, and the migration

The two rows the pipeline writes, plus their EF mapping. The unique index on `MessageId` is the only thing standing between at-least-once delivery and a double-notified user, so it gets a test that proves the *database* rejects the duplicate.

**Files:**
- Create: `src/MarketPulse.Domain/Entities/Notification.cs`
- Create: `src/MarketPulse.Domain/Entities/OutboxMessage.cs`
- Modify: `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/Migrations/<timestamp>_AddAlerts.cs` (generated)
- Test: `tests/MarketPulse.IntegrationTests/AlertPersistenceTests.cs`

**Interfaces:**
- Consumes: `AlertRule`, `AlertDirection`, `AlertRuleStatus` from Task 1.
- Produces: `Notification.Create(Guid messageId, Guid userId, Guid alertRuleId, string ticker, AlertDirection direction, decimal threshold, decimal triggeredPrice, DateTimeOffset occurredUtc, DateTimeOffset now)` and `void MarkRead()`; `OutboxMessage.Create(Guid messageId, string type, string payload, string? correlationId, DateTimeOffset occurredUtc)`, `void MarkDispatched(DateTimeOffset now)`, `void RecordAttempt()`. `MarketPulseDbContext.AlertRules`, `.Notifications`, `.OutboxMessages`.

- [ ] **Step 1: Write the failing test**

Create `tests/MarketPulse.IntegrationTests/AlertPersistenceTests.cs`:

```csharp
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class AlertPersistenceTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> NewUserAsync()
    {
        await using var db = fixture.CreateContext();
        var user = User.Register($"persist-{Guid.NewGuid():N}@marketpulse.local", "hash");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task An_alert_rule_round_trips()
    {
        var userId = await NewUserAsync();
        var rule = AlertRule.Create(userId, "IVV", AlertDirection.Above, 50m, Now);

        await using (var db = fixture.CreateContext())
        {
            db.AlertRules.Add(rule);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var loaded = await db.AlertRules.SingleAsync(r => r.Id == rule.Id);

            Assert.Equal("IVV", loaded.Ticker);
            Assert.Equal(AlertDirection.Above, loaded.Direction);
            Assert.Equal(50m, loaded.Threshold);
            Assert.Equal(AlertRuleStatus.Active, loaded.Status);
            Assert.NotEmpty(loaded.RowVersion);
        }
    }

    [Fact]
    public async Task A_stale_row_version_loses_the_race()
    {
        var userId = await NewUserAsync();
        var rule = AlertRule.Create(userId, "NDQ", AlertDirection.Above, 50m, Now);

        await using (var db = fixture.CreateContext())
        {
            db.AlertRules.Add(rule);
            await db.SaveChangesAsync();
        }

        // Two contexts load the same rule — this is two worker instances handling two
        // ticks. The second save must fail rather than double-trigger.
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();

        var a = await first.AlertRules.SingleAsync(r => r.Id == rule.Id);
        var b = await second.AlertRules.SingleAsync(r => r.Id == rule.Id);

        a.MarkTriggered(51m, Now);
        await first.SaveChangesAsync();

        b.MarkTriggered(52m, Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task The_database_rejects_a_second_notification_with_the_same_message_id()
    {
        var userId = await NewUserAsync();
        var messageId = Guid.NewGuid();

        Notification Build() => Notification.Create(
            messageId, userId, Guid.NewGuid(), "IVV",
            AlertDirection.Above, 50m, 51m, Now, Now);

        await using (var db = fixture.CreateContext())
        {
            db.Notifications.Add(Build());
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            db.Notifications.Add(Build());

            // The dedupe is enforced by a unique index, not by a read-then-write that
            // races itself. If this ever stops throwing, redelivery double-notifies.
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task An_outbox_message_round_trips_and_marks_dispatched()
    {
        var message = OutboxMessage.Create(
            Guid.NewGuid(), "AlertTriggered", """{"ticker":"IVV"}""", "corr-1", Now);

        await using (var db = fixture.CreateContext())
        {
            db.OutboxMessages.Add(message);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            var pending = await db.OutboxMessages
                .Where(m => m.DispatchedUtc == null)
                .SingleAsync(m => m.Id == message.Id);

            Assert.Equal("corr-1", pending.CorrelationId);
            Assert.Equal(0, pending.AttemptCount);

            pending.MarkDispatched(Now);
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateContext())
        {
            Assert.False(await db.OutboxMessages
                .AnyAsync(m => m.Id == message.Id && m.DispatchedUtc == null));
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~AlertPersistenceTests`
Expected: compile failure — `Notification`, `OutboxMessage` and the three `DbSet`s do not exist.

- [ ] **Step 3: Write the two entities**

Create `src/MarketPulse.Domain/Entities/Notification.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

/// <summary>
/// A triggered alert, delivered. The rule's details are snapshotted rather than joined, so
/// deleting a rule does not rewrite the user's history.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; private set; }

    /// <summary>
    /// The integration message's id, carried from the outbox row. Uniquely indexed: this
    /// is the single point at which at-least-once delivery becomes exactly-once storage.
    /// </summary>
    public Guid MessageId { get; private set; }

    public Guid UserId { get; private set; }
    public Guid AlertRuleId { get; private set; }
    public string Ticker { get; private set; } = string.Empty;
    public AlertDirection Direction { get; private set; }
    public decimal Threshold { get; private set; }
    public decimal TriggeredPrice { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public bool IsRead { get; private set; }

    private Notification() { }

    public static Notification Create(
        Guid messageId,
        Guid userId,
        Guid alertRuleId,
        string ticker,
        AlertDirection direction,
        decimal threshold,
        decimal triggeredPrice,
        DateTimeOffset occurredUtc,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = messageId,
        UserId = userId,
        AlertRuleId = alertRuleId,
        Ticker = ticker,
        Direction = direction,
        Threshold = threshold,
        TriggeredPrice = triggeredPrice,
        OccurredUtc = occurredUtc,
        CreatedUtc = now
    };

    public void MarkRead() => IsRead = true;
}
```

Create `src/MarketPulse.Domain/Entities/OutboxMessage.cs`:

```csharp
namespace MarketPulse.Domain.Entities;

/// <summary>
/// One integration event, written in the same transaction as the state change that caused
/// it. The dispatcher relays it and marks it dispatched only once the broker confirms.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>Also the message id on the wire, and the consumer's dedupe key.</summary>
    public Guid Id { get; private set; }

    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string? CorrelationId { get; private set; }
    public DateTimeOffset OccurredUtc { get; private set; }
    public DateTimeOffset? DispatchedUtc { get; private set; }
    public int AttemptCount { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create(
        Guid messageId,
        string type,
        string payload,
        string? correlationId,
        DateTimeOffset occurredUtc) => new()
    {
        Id = messageId,
        Type = type,
        Payload = payload,
        CorrelationId = correlationId,
        OccurredUtc = occurredUtc
    };

    public void MarkDispatched(DateTimeOffset now) => DispatchedUtc ??= now;

    public void RecordAttempt() => AttemptCount++;
}
```

- [ ] **Step 4: Map them in the DbContext**

In `src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs`, add three `DbSet`s beside the existing four:

```csharp
public DbSet<AlertRule> AlertRules => Set<AlertRule>();
public DbSet<Notification> Notifications => Set<Notification>();
public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
```

and add these three configurations at the end of `OnModelCreating`:

```csharp
b.Entity<AlertRule>(e =>
{
    e.HasKey(x => x.Id);
    e.Property(x => x.Ticker).HasMaxLength(8).IsRequired();
    e.Property(x => x.Threshold).HasPrecision(18, 4);
    e.Property(x => x.TriggeredPrice).HasPrecision(18, 4);

    // Stored as strings. An enum persisted as an int is unreadable in a query window and
    // silently reorders if a member is ever inserted in the middle.
    e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(8).IsRequired();
    e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

    e.Property(x => x.RowVersion).IsRowVersion();

    // The worker's hot query: every tick asks for one ticker's active rules.
    e.HasIndex(x => new { x.Ticker, x.Status });

    // The API's query: one user's rules.
    e.HasIndex(x => x.UserId);

    e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);
});

b.Entity<Notification>(e =>
{
    e.HasKey(x => x.Id);

    // The dedupe. Not an optimisation — the correctness of the whole delivery path.
    e.HasIndex(x => x.MessageId).IsUnique();

    e.Property(x => x.Ticker).HasMaxLength(8).IsRequired();
    e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(8).IsRequired();
    e.Property(x => x.Threshold).HasPrecision(18, 4);
    e.Property(x => x.TriggeredPrice).HasPrecision(18, 4);

    // The panel's query: newest first, for one user.
    e.HasIndex(x => new { x.UserId, x.CreatedUtc });

    e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
        .OnDelete(DeleteBehavior.Cascade);
});

b.Entity<OutboxMessage>(e =>
{
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).ValueGeneratedNever();
    e.Property(x => x.Type).HasMaxLength(128).IsRequired();
    e.Property(x => x.Payload).IsRequired();
    e.Property(x => x.CorrelationId).HasMaxLength(128);

    // Filtered: the dispatcher polls twice a second forever, and this table only grows.
    // An unfiltered index would still make it scan every dispatched row ever written.
    e.HasIndex(x => x.OccurredUtc)
        .HasFilter("[DispatchedUtc] IS NULL")
        .HasDatabaseName("IX_OutboxMessages_Pending");
});
```

- [ ] **Step 5: Generate the migration**

Run:

```bash
dotnet ef migrations add AddAlerts --project src/MarketPulse.Infrastructure --startup-project src/MarketPulse.Api
```

Open the generated `Up` method and confirm it creates three tables, the `IX_OutboxMessages_Pending` filtered index, the unique index on `Notifications.MessageId`, and a `rowversion` column on `AlertRules`. If any is missing, the mapping above is wrong — fix the mapping and regenerate rather than hand-editing the migration.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~AlertPersistenceTests`
Expected: PASS, 4 tests. Testcontainers applies the new migration automatically via `SqlServerFixture.InitializeAsync`.

- [ ] **Step 7: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add src/MarketPulse.Domain src/MarketPulse.Infrastructure tests/MarketPulse.IntegrationTests/AlertPersistenceTests.cs
git commit -m "feat(persistence): add Notification and OutboxMessage with the AddAlerts migration"
```

---

### Task 3: Alert-rule CRUD

The repository, the four handlers, and the controller. This task is the first that a user can see, and it ships the two invariants the aggregate could not enforce: the per-user limit and the unknown-ticker check.

**Files:**
- Create: `src/MarketPulse.Application/Abstractions/IAlertRuleRepository.cs`
- Create: `src/MarketPulse.Application/Alerts/CreateAlertRuleCommand.cs`
- Create: `src/MarketPulse.Application/Alerts/GetAlertRulesQuery.cs`
- Create: `src/MarketPulse.Application/Alerts/DeleteAlertRuleCommand.cs`
- Create: `src/MarketPulse.Application/Alerts/RearmAlertRuleCommand.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/AlertRuleRepository.cs`
- Create: `src/MarketPulse.Api/Controllers/AlertsController.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Test: `tests/MarketPulse.UnitTests/Application/CreateAlertRuleHandlerTests.cs`
- Test: `tests/MarketPulse.IntegrationTests/AlertsApiTests.cs`

**Interfaces:**
- Consumes: `AlertRule`, `AlertDirection`, `AlertRuleStatus`, the alert exceptions (Task 1); `MarketPulseDbContext.AlertRules` (Task 2); the existing `ICurrentUser`.
- Produces: `IAlertRuleRepository` with `GetForUserAsync(Guid, CancellationToken)`, `GetByIdAsync(Guid id, Guid userId, CancellationToken)`, `CountForUserAsync(Guid, CancellationToken)`, `ActiveDuplicateExistsAsync(Guid userId, string ticker, AlertDirection, decimal threshold, CancellationToken)`, `GetActiveForTickerAsync(string, CancellationToken)`, `AddAsync`, `Remove`, `TickerExistsAsync`, `SaveChangesAsync`. `AlertRuleDto(Guid Id, string Ticker, string Direction, decimal Threshold, string Status, DateTimeOffset CreatedUtc, DateTimeOffset? TriggeredUtc, decimal? TriggeredPrice)`. `CreateAlertRuleCommand(string Ticker, string Direction, decimal Threshold)`.

- [ ] **Step 1: Write the failing handler unit test**

Create `tests/MarketPulse.UnitTests/Application/CreateAlertRuleHandlerTests.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Alerts;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using NSubstitute;

namespace MarketPulse.UnitTests.Application;

public class CreateAlertRuleHandlerTests
{
    private readonly IAlertRuleRepository _repo = Substitute.For<IAlertRuleRepository>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Guid _userId = Guid.NewGuid();

    public CreateAlertRuleHandlerTests() => _user.UserId.Returns(_userId);

    private CreateAlertRuleHandler Handler() => new(_repo, _user);

    private static CreateAlertRuleCommand Command(string direction = "Above") =>
        new("IVV", direction, 50m);

    [Fact]
    public async Task A_rule_is_created_and_saved()
    {
        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.Equal("IVV", result.Ticker);
        Assert.Equal("Above", result.Direction);
        Assert.Equal("Active", result.Status);
        await _repo.Received(1).AddAsync(Arg.Any<AlertRule>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_twenty_first_rule_is_rejected()
    {
        _repo.CountForUserAsync(_userId, Arg.Any<CancellationToken>())
             .Returns(AlertRule.MaxPerUser);

        await Assert.ThrowsAsync<AlertRuleLimitException>(
            () => Handler().Handle(Command(), CancellationToken.None));

        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_identical_active_rule_is_rejected()
    {
        _repo.ActiveDuplicateExistsAsync(
                _userId, "IVV", AlertDirection.Above, 50m, Arg.Any<CancellationToken>())
             .Returns(true);

        await Assert.ThrowsAsync<DuplicateAlertRuleException>(
            () => Handler().Handle(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task The_limit_is_checked_before_the_duplicate_check()
    {
        // Both fail. The user has a real ceiling problem and a cosmetic one; tell them
        // about the ceiling, because deleting the duplicate would not help them.
        _repo.CountForUserAsync(_userId, Arg.Any<CancellationToken>())
             .Returns(AlertRule.MaxPerUser);
        _repo.ActiveDuplicateExistsAsync(
                _userId, "IVV", AlertDirection.Above, 50m, Arg.Any<CancellationToken>())
             .Returns(true);

        await Assert.ThrowsAsync<AlertRuleLimitException>(
            () => Handler().Handle(Command(), CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~CreateAlertRuleHandlerTests`
Expected: compile failure — `IAlertRuleRepository`, `CreateAlertRuleCommand`, `CreateAlertRuleHandler` do not exist.

- [ ] **Step 3: Write the repository abstraction**

Create `src/MarketPulse.Application/Abstractions/IAlertRuleRepository.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface IAlertRuleRepository
{
    Task<IReadOnlyList<AlertRule>> GetForUserAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Scoped by user on purpose: a rule belonging to someone else must be indistinguishable
    /// from one that does not exist.
    /// </summary>
    Task<AlertRule?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct);

    Task<int> CountForUserAsync(Guid userId, CancellationToken ct);

    Task<bool> ActiveDuplicateExistsAsync(
        Guid userId, string ticker, AlertDirection direction, decimal threshold,
        CancellationToken ct);

    /// <summary>The worker's per-tick query. Not scoped by user — it evaluates everyone's.</summary>
    Task<IReadOnlyList<AlertRule>> GetActiveForTickerAsync(string ticker, CancellationToken ct);

    Task AddAsync(AlertRule rule, CancellationToken ct);
    void Remove(AlertRule rule);
    Task<bool> TickerExistsAsync(string code, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
```

- [ ] **Step 4: Write the create command, validator and handler**

Create `src/MarketPulse.Application/Alerts/CreateAlertRuleCommand.cs`:

```csharp
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record AlertRuleDto(
    Guid Id,
    string Ticker,
    string Direction,
    decimal Threshold,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? TriggeredUtc,
    decimal? TriggeredPrice);

/// <summary>
/// Direction arrives as a string rather than the enum so that a bad value is a 400 with a
/// readable message, not a model-binding failure that bypasses the validator.
/// </summary>
public record CreateAlertRuleCommand(string Ticker, string Direction, decimal Threshold)
    : IRequest<AlertRuleDto>;

public sealed class CreateAlertRuleValidator : AbstractValidator<CreateAlertRuleCommand>
{
    public CreateAlertRuleValidator(IAlertRuleRepository repo)
    {
        RuleFor(x => x.Ticker)
            .NotEmpty().WithMessage("Ticker is required.").WithErrorCode("invalid-ticker")
            .MaximumLength(8).WithMessage("Ticker must be 8 characters or fewer.")
                .WithErrorCode("invalid-ticker")
            .MustAsync(async (ticker, ct) =>
                await repo.TickerExistsAsync(ticker.Trim().ToUpperInvariant(), ct))
            .WithMessage(x => $"'{x.Ticker}' is not a known ticker.")
            .WithErrorCode("unknown-ticker");

        RuleFor(x => x.Direction)
            .Must(d => Enum.TryParse<AlertDirection>(d, ignoreCase: true, out _))
            .WithMessage("Direction must be 'Above' or 'Below'.")
            .WithErrorCode("invalid-direction");

        RuleFor(x => x.Threshold)
            .GreaterThan(0).WithMessage("An alert threshold must be greater than zero.")
            .WithErrorCode("invalid-threshold");
    }
}

public sealed class CreateAlertRuleHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<CreateAlertRuleCommand, AlertRuleDto>
{
    public async Task<AlertRuleDto> Handle(CreateAlertRuleCommand request, CancellationToken ct)
    {
        var ticker = request.Ticker.Trim().ToUpperInvariant();
        var direction = Enum.Parse<AlertDirection>(request.Direction, ignoreCase: true);

        // Neither of these can live on the aggregate: a standalone rule can see neither its
        // siblings nor the ticker table. See the spec's domain-model section.
        if (await repo.CountForUserAsync(user.UserId, ct) >= AlertRule.MaxPerUser)
        {
            throw new AlertRuleLimitException(AlertRule.MaxPerUser);
        }

        if (await repo.ActiveDuplicateExistsAsync(
                user.UserId, ticker, direction, request.Threshold, ct))
        {
            throw new DuplicateAlertRuleException(ticker);
        }

        var rule = AlertRule.Create(
            user.UserId, ticker, direction, request.Threshold, DateTimeOffset.UtcNow);

        await repo.AddAsync(rule, ct);
        await repo.SaveChangesAsync(ct);

        return rule.ToDto();
    }
}

internal static class AlertRuleMappings
{
    public static AlertRuleDto ToDto(this AlertRule r) => new(
        r.Id, r.Ticker, r.Direction.ToString(), r.Threshold, r.Status.ToString(),
        r.CreatedUtc, r.TriggeredUtc, r.TriggeredPrice);
}
```

- [ ] **Step 5: Write the query and the two remaining commands**

Create `src/MarketPulse.Application/Alerts/GetAlertRulesQuery.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record GetAlertRulesQuery : IRequest<IReadOnlyList<AlertRuleDto>>;

public sealed class GetAlertRulesHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<GetAlertRulesQuery, IReadOnlyList<AlertRuleDto>>
{
    public async Task<IReadOnlyList<AlertRuleDto>> Handle(
        GetAlertRulesQuery request, CancellationToken ct)
    {
        var rules = await repo.GetForUserAsync(user.UserId, ct);
        return rules.Select(r => r.ToDto()).ToList();
    }
}
```

Create `src/MarketPulse.Application/Alerts/DeleteAlertRuleCommand.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record DeleteAlertRuleCommand(Guid Id) : IRequest;

public sealed class DeleteAlertRuleHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<DeleteAlertRuleCommand>
{
    public async Task Handle(DeleteAlertRuleCommand request, CancellationToken ct)
    {
        var rule = await repo.GetByIdAsync(request.Id, user.UserId, ct)
            ?? throw new AlertRuleNotFoundException();

        repo.Remove(rule);
        await repo.SaveChangesAsync(ct);
    }
}
```

Create `src/MarketPulse.Application/Alerts/RearmAlertRuleCommand.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Alerts;

public record RearmAlertRuleCommand(Guid Id) : IRequest<AlertRuleDto>;

public sealed class RearmAlertRuleHandler(IAlertRuleRepository repo, ICurrentUser user)
    : IRequestHandler<RearmAlertRuleCommand, AlertRuleDto>
{
    public async Task<AlertRuleDto> Handle(RearmAlertRuleCommand request, CancellationToken ct)
    {
        var rule = await repo.GetByIdAsync(request.Id, user.UserId, ct)
            ?? throw new AlertRuleNotFoundException();

        // Throws AlertNotTriggeredException if it was never fired.
        rule.Rearm();
        await repo.SaveChangesAsync(ct);

        return rule.ToDto();
    }
}
```

- [ ] **Step 6: Write the EF repository and register it**

Create `src/MarketPulse.Infrastructure/Persistence/AlertRuleRepository.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class AlertRuleRepository(MarketPulseDbContext db) : IAlertRuleRepository
{
    public async Task<IReadOnlyList<AlertRule>> GetForUserAsync(Guid userId, CancellationToken ct) =>
        await db.AlertRules
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedUtc)
            .ToListAsync(ct);

    public Task<AlertRule?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.AlertRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);

    public Task<int> CountForUserAsync(Guid userId, CancellationToken ct) =>
        db.AlertRules.CountAsync(r => r.UserId == userId, ct);

    public Task<bool> ActiveDuplicateExistsAsync(
        Guid userId, string ticker, AlertDirection direction, decimal threshold,
        CancellationToken ct) =>
        db.AlertRules.AnyAsync(
            r => r.UserId == userId
              && r.Ticker == ticker
              && r.Direction == direction
              && r.Threshold == threshold
              && r.Status == AlertRuleStatus.Active,
            ct);

    public async Task<IReadOnlyList<AlertRule>> GetActiveForTickerAsync(
        string ticker, CancellationToken ct) =>
        await db.AlertRules
            .Where(r => r.Ticker == ticker && r.Status == AlertRuleStatus.Active)
            .ToListAsync(ct);

    public async Task AddAsync(AlertRule rule, CancellationToken ct) =>
        await db.AlertRules.AddAsync(rule, ct);

    public void Remove(AlertRule rule) => db.AlertRules.Remove(rule);

    public Task<bool> TickerExistsAsync(string code, CancellationToken ct) =>
        db.Tickers.AnyAsync(t => t.Code == code, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
```

In `src/MarketPulse.Infrastructure/DependencyInjection.cs`, add beside the existing repository registrations:

```csharp
services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
```

- [ ] **Step 7: Write the controller**

Create `src/MarketPulse.Api/Controllers/AlertsController.cs`:

```csharp
using MarketPulse.Application.Alerts;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/alerts")]
public sealed class AlertsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AlertRuleDto>>> Get(CancellationToken ct) =>
        Ok(await sender.Send(new GetAlertRulesQuery(), ct));

    [HttpPost]
    public async Task<ActionResult<AlertRuleDto>> Create(
        [FromBody] CreateAlertRuleCommand command, CancellationToken ct)
    {
        var rule = await sender.Send(command, ct);
        return CreatedAtAction(nameof(Get), new { id = rule.Id }, rule);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await sender.Send(new DeleteAlertRuleCommand(id), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/rearm")]
    public async Task<ActionResult<AlertRuleDto>> Rearm(Guid id, CancellationToken ct) =>
        Ok(await sender.Send(new RearmAlertRuleCommand(id), ct));
}
```

- [ ] **Step 8: Run the unit test to verify it passes**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~CreateAlertRuleHandlerTests`
Expected: PASS, 4 tests.

- [ ] **Step 9: Write the API integration test**

Create `tests/MarketPulse.IntegrationTests/AlertsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class AlertsApiTests(SqlServerFixture fixture)
{
    private sealed record RuleResponse(
        Guid Id, string Ticker, string Direction, decimal Threshold, string Status);

    private static object NewRule(
        string ticker = "IVV", string direction = "Above", decimal threshold = 50m) =>
        new { Ticker = ticker, Direction = direction, Threshold = threshold };

    [Fact]
    public async Task A_rule_can_be_created_and_listed()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var created = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var rules = await client.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts");

        var rule = Assert.Single(rules!);
        Assert.Equal("IVV", rule.Ticker);
        Assert.Equal("Active", rule.Status);
    }

    [Fact]
    public async Task An_unknown_ticker_is_rejected()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync("/api/v1/alerts", NewRule(ticker: "ZZZZ"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_threshold_of_zero_is_rejected()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var response = await client.PostAsJsonAsync("/api/v1/alerts", NewRule(threshold: 0m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_identical_active_rule_is_a_conflict()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var second = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task The_twenty_first_rule_is_a_conflict()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        // Twenty distinct thresholds, so each one clears the duplicate check.
        for (var i = 1; i <= 20; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/alerts", NewRule(threshold: i));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var overflow = await client.PostAsJsonAsync("/api/v1/alerts", NewRule(threshold: 21m));

        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode);
    }

    [Fact]
    public async Task A_rule_can_be_deleted()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var created = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var rule = await created.Content.ReadFromJsonAsync<RuleResponse>();

        var deleted = await client.DeleteAsync($"/api/v1/alerts/{rule!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var rules = await client.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts");
        Assert.Empty(rules!);
    }

    [Fact]
    public async Task Re_arming_a_rule_that_never_fired_is_a_conflict()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        var created = await client.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var rule = await created.Content.ReadFromJsonAsync<RuleResponse>();

        var rearm = await client.PostAsync($"/api/v1/alerts/{rule!.Id}/rearm", null);

        Assert.Equal(HttpStatusCode.Conflict, rearm.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        await using var factory = TestFactory.Create(fixture);

        var response = await factory.CreateClient().GetAsync("/api/v1/alerts");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task One_user_cannot_see_or_touch_another_users_rule()
    {
        await using var factory = TestFactory.Create(fixture);
        var alice = await AuthenticatedClient.RegisterAsync(factory);
        var bob = await AuthenticatedClient.RegisterAsync(factory);

        var created = await alice.PostAsJsonAsync("/api/v1/alerts", NewRule());
        var rule = await created.Content.ReadFromJsonAsync<RuleResponse>();

        Assert.Empty((await bob.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts"))!);

        // 404, not 403: confirming the id exists would be an information leak.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.DeleteAsync($"/api/v1/alerts/{rule!.Id}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/v1/alerts/{rule.Id}/rearm", null)).StatusCode);

        // And Alice's rule is still there.
        Assert.Single((await alice.GetFromJsonAsync<List<RuleResponse>>("/api/v1/alerts"))!);
    }
}
```

- [ ] **Step 10: Run the integration test to verify it passes**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~AlertsApiTests`
Expected: PASS, 9 tests.

- [ ] **Step 11: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add src/MarketPulse.Application src/MarketPulse.Infrastructure src/MarketPulse.Api tests
git commit -m "feat(alerts): add alert-rule CRUD with per-user limit and ownership scoping"
```

---

### Task 4: The notifications read API

Small, and it completes the HTTP surface so that Task 9's end-to-end test has something to assert against without reaching into the database.

**Files:**
- Create: `src/MarketPulse.Application/Abstractions/INotificationRepository.cs`
- Create: `src/MarketPulse.Application/Notifications/GetNotificationsQuery.cs`
- Create: `src/MarketPulse.Application/Notifications/MarkNotificationReadCommand.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/NotificationRepository.cs`
- Create: `src/MarketPulse.Api/Controllers/NotificationsController.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Test: `tests/MarketPulse.IntegrationTests/NotificationsApiTests.cs`

**Interfaces:**
- Consumes: `Notification` (Task 2), `ICurrentUser`, `NotificationNotFoundException` (Task 1).
- Produces: `INotificationRepository` with `GetForUserAsync(Guid userId, int skip, int take, CancellationToken)`, `GetByIdAsync(Guid id, Guid userId, CancellationToken)`, `AddAsync`, `SaveChangesAsync`. `NotificationDto(Guid Id, Guid AlertRuleId, string Ticker, string Direction, decimal Threshold, decimal TriggeredPrice, DateTimeOffset OccurredUtc, bool IsRead)`.

- [ ] **Step 1: Write the failing test**

Create `tests/MarketPulse.IntegrationTests/NotificationsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class NotificationsApiTests(SqlServerFixture fixture)
{
    private sealed record NotificationResponse(
        Guid Id, string Ticker, decimal TriggeredPrice, bool IsRead);

    /// <summary>
    /// Seeds a notification directly. Task 9 wires the real path; this test is about the
    /// read side, and going through the broker to test a GET would be a worse test.
    /// </summary>
    private async Task<Guid> SeedAsync(string email, DateTimeOffset occurredUtc)
    {
        await using var db = fixture.CreateContext();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();

        var notification = Notification.Create(
            Guid.NewGuid(), userId, Guid.NewGuid(), "IVV",
            AlertDirection.Above, 50m, 51m, occurredUtc, occurredUtc);

        db.Notifications.Add(notification);
        await db.SaveChangesAsync();
        return notification.Id;
    }

    [Fact]
    public async Task Notifications_come_back_newest_first()
    {
        await using var factory = TestFactory.Create(fixture);
        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(factory, email);

        var older = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedAsync(email, older);
        var newerId = await SeedAsync(email, older.AddHours(1));

        var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications");

        Assert.Equal(2, list!.Count);
        Assert.Equal(newerId, list[0].Id);
    }

    [Fact]
    public async Task Take_is_capped_at_one_hundred()
    {
        await using var factory = TestFactory.Create(fixture);
        var client = await AuthenticatedClient.RegisterAsync(factory);

        // An uncapped take is a denial-of-service handed to any authenticated caller.
        var response = await client.GetAsync("/api/v1/notifications?take=5000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_notification_can_be_marked_read()
    {
        await using var factory = TestFactory.Create(fixture);
        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(factory, email);

        var id = await SeedAsync(email, DateTimeOffset.UtcNow);

        var marked = await client.PostAsync($"/api/v1/notifications/{id}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);

        var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications");

        Assert.True(Assert.Single(list!).IsRead);
    }

    [Fact]
    public async Task One_user_cannot_see_or_read_another_users_notification()
    {
        await using var factory = TestFactory.Create(fixture);
        var aliceEmail = AuthenticatedClient.NewEmail();
        await AuthenticatedClient.RegisterAsync(factory, aliceEmail);
        var bob = await AuthenticatedClient.RegisterAsync(factory);

        var id = await SeedAsync(aliceEmail, DateTimeOffset.UtcNow);

        Assert.Empty((await bob.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications"))!);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/v1/notifications/{id}/read", null)).StatusCode);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~NotificationsApiTests`
Expected: 404s and compile failures — the endpoints do not exist.

- [ ] **Step 3: Write the abstraction, query and command**

Create `src/MarketPulse.Application/Abstractions/INotificationRepository.cs`:

```csharp
using MarketPulse.Domain.Entities;

namespace MarketPulse.Application.Abstractions;

public interface INotificationRepository
{
    Task<IReadOnlyList<Notification>> GetForUserAsync(
        Guid userId, int skip, int take, CancellationToken ct);

    Task<Notification?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct);
    Task AddAsync(Notification notification, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
```

Create `src/MarketPulse.Application/Notifications/GetNotificationsQuery.cs`:

```csharp
using FluentValidation;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MediatR;

namespace MarketPulse.Application.Notifications;

public record NotificationDto(
    Guid Id,
    Guid AlertRuleId,
    string Ticker,
    string Direction,
    decimal Threshold,
    decimal TriggeredPrice,
    DateTimeOffset OccurredUtc,
    bool IsRead);

public record GetNotificationsQuery(int Skip = 0, int Take = 50)
    : IRequest<IReadOnlyList<NotificationDto>>;

public sealed class GetNotificationsValidator : AbstractValidator<GetNotificationsQuery>
{
    public GetNotificationsValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0)
            .WithMessage("Skip cannot be negative.").WithErrorCode("invalid-paging");

        // Capped, not clamped: silently returning 100 when 5000 was asked for is a lie the
        // caller cannot detect.
        RuleFor(x => x.Take).InclusiveBetween(1, 100)
            .WithMessage("Take must be between 1 and 100.").WithErrorCode("invalid-paging");
    }
}

public sealed class GetNotificationsHandler(INotificationRepository repo, ICurrentUser user)
    : IRequestHandler<GetNotificationsQuery, IReadOnlyList<NotificationDto>>
{
    public async Task<IReadOnlyList<NotificationDto>> Handle(
        GetNotificationsQuery request, CancellationToken ct)
    {
        var notifications = await repo.GetForUserAsync(
            user.UserId, request.Skip, request.Take, ct);

        return notifications.Select(n => n.ToDto()).ToList();
    }
}

internal static class NotificationMappings
{
    public static NotificationDto ToDto(this Notification n) => new(
        n.Id, n.AlertRuleId, n.Ticker, n.Direction.ToString(), n.Threshold,
        n.TriggeredPrice, n.OccurredUtc, n.IsRead);
}
```

Create `src/MarketPulse.Application/Notifications/MarkNotificationReadCommand.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Exceptions;
using MediatR;

namespace MarketPulse.Application.Notifications;

public record MarkNotificationReadCommand(Guid Id) : IRequest;

public sealed class MarkNotificationReadHandler(
    INotificationRepository repo, ICurrentUser user)
    : IRequestHandler<MarkNotificationReadCommand>
{
    public async Task Handle(MarkNotificationReadCommand request, CancellationToken ct)
    {
        var notification = await repo.GetByIdAsync(request.Id, user.UserId, ct)
            ?? throw new NotificationNotFoundException();

        notification.MarkRead();
        await repo.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 4: Write the repository, register it, and add the controller**

Create `src/MarketPulse.Infrastructure/Persistence/NotificationRepository.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Infrastructure.Persistence;

public sealed class NotificationRepository(MarketPulseDbContext db) : INotificationRepository
{
    public async Task<IReadOnlyList<Notification>> GetForUserAsync(
        Guid userId, int skip, int take, CancellationToken ct) =>
        await db.Notifications
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

    public Task<Notification?> GetByIdAsync(Guid id, Guid userId, CancellationToken ct) =>
        db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId, ct);

    public async Task AddAsync(Notification notification, CancellationToken ct) =>
        await db.Notifications.AddAsync(notification, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
```

In `src/MarketPulse.Infrastructure/DependencyInjection.cs`:

```csharp
services.AddScoped<INotificationRepository, NotificationRepository>();
```

Create `src/MarketPulse.Api/Controllers/NotificationsController.cs`:

```csharp
using MarketPulse.Application.Notifications;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketPulse.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NotificationDto>>> Get(
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        Ok(await sender.Send(new GetNotificationsQuery(skip, take), ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        await sender.Send(new MarkNotificationReadCommand(id), ct);
        return NoContent();
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~NotificationsApiTests`
Expected: PASS, 4 tests.

- [ ] **Step 6: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add src/MarketPulse.Application src/MarketPulse.Infrastructure src/MarketPulse.Api tests
git commit -m "feat(notifications): add the notifications read API with capped paging"
```

---

### Task 5: RabbitMQ connection, options and topology

No business behaviour — this is the broker plumbing both the API and the worker sit on. It is its own task because a reviewer could reasonably reject the topology while approving everything around it.

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `docker-compose.yml`
- Modify: `src/MarketPulse.Infrastructure/MarketPulse.Infrastructure.csproj`
- Modify: `tests/MarketPulse.IntegrationTests/MarketPulse.IntegrationTests.csproj`
- Create: `src/MarketPulse.Application/Configuration/RabbitMqOptions.cs`
- Create: `src/MarketPulse.Infrastructure/Messaging/RabbitMqConnection.cs`
- Create: `src/MarketPulse.Infrastructure/Messaging/RabbitMqTopology.cs`
- Create: `src/MarketPulse.Infrastructure/Messaging/Contracts/PriceTickMessage.cs`
- Create: `src/MarketPulse.Infrastructure/Messaging/Contracts/AlertTriggeredMessage.cs`
- Create: `tests/MarketPulse.IntegrationTests/RabbitMqFixture.cs`
- Test: `tests/MarketPulse.IntegrationTests/RabbitMqTopologyTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `RabbitMqOptions` (`SectionName = "RabbitMq"`, `HostName`, `Port`, `UserName`, `Password`, `VirtualHost`, `PricesExchange`, `AlertsExchange`, `AlertsDeadLetterExchange`, `PricesQueue`, `NotificationsQueue`, `NotificationsDeadLetterQueue`, `TickTtlMilliseconds`, `TickQueueMaxLength`, `PrefetchCount`); `RabbitMqConnection` with `Task<IConnection> GetAsync(CancellationToken)` and `Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken)`; `RabbitMqTopology.DeclareAsync(IChannel, RabbitMqOptions, CancellationToken)` and `RabbitMqTopology.RoutingKeyFor(string ticker)`; contracts `PriceTickMessage` and `AlertTriggeredMessage`.

- [ ] **Step 1: Add the packages**

Run:

```bash
dotnet add src/MarketPulse.Infrastructure package RabbitMQ.Client
dotnet add tests/MarketPulse.IntegrationTests package Testcontainers.RabbitMq
```

Central package management moves the versions into `Directory.Packages.props` and leaves bare
`<PackageReference Include="…" />` entries in the two `.csproj` files. Confirm that happened;
if a `Version` attribute landed in a `.csproj`, move it.

**The code in this plan uses the RabbitMQ.Client v7 async API** — `IChannel` (not `IModel`),
`CreateConnectionAsync`, `ExchangeDeclareAsync`, `BasicPublishAsync`, `BasicConsumeAsync`,
`AsyncEventingBasicConsumer.ReceivedAsync`. If `dotnet add` resolved a 6.x package, the
synchronous API will not compile against these snippets — pin 7.x in
`Directory.Packages.props` rather than rewriting the code.

Pin `Testcontainers.RabbitMq` to the same version as the existing `Testcontainers.MsSql`
(4.13.0). The modules share a core assembly and mismatched versions fail at runtime.

- [ ] **Step 2: Add RabbitMQ to docker-compose**

In `docker-compose.yml`, add a second service beside `sqlserver`:

```yaml
  rabbitmq:
    image: rabbitmq:3-management
    container_name: marketpulse-rabbit
    environment:
      RABBITMQ_DEFAULT_USER: marketpulse
      RABBITMQ_DEFAULT_PASS: Local!Dev!Pass123
    ports:
      - "5672:5672"
      - "15672:15672"
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
      interval: 10s
      timeout: 5s
      retries: 12
      start_period: 20s
```

- [ ] **Step 3: Write the failing topology test**

Create `tests/MarketPulse.IntegrationTests/RabbitMqFixture.cs`:

```csharp
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace MarketPulse.IntegrationTests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private readonly RabbitMqContainer _container =
        new RabbitMqBuilder().WithImage("rabbitmq:3-management").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task<IConnection> ConnectAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(ConnectionString) };
        return await factory.CreateConnectionAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>
/// Both containers in one collection so a test class that needs the API (SQL Server) and
/// the broker together gets them from a single fixture lifetime rather than starting a
/// second copy of each.
/// </summary>
[CollectionDefinition(nameof(MessagingCollection))]
public sealed class MessagingCollection
    : ICollectionFixture<SqlServerFixture>, ICollectionFixture<RabbitMqFixture>;
```

Create `tests/MarketPulse.IntegrationTests/RabbitMqTopologyTests.cs`:

```csharp
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(MessagingCollection))]
public class RabbitMqTopologyTests(RabbitMqFixture rabbit)
{
    private static RabbitMqOptions Options() => new();

    [Fact]
    public async Task Declaring_the_topology_twice_succeeds()
    {
        // Both the API and the worker declare at startup, in whatever order they happen to
        // boot. AMQP declarations are idempotent; this proves ours actually are, which
        // means neither process has to wait for the other.
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        await RabbitMqTopology.DeclareAsync(channel, Options(), CancellationToken.None);
        await RabbitMqTopology.DeclareAsync(channel, Options(), CancellationToken.None);
    }

    [Fact]
    public async Task A_tick_published_to_the_prices_exchange_lands_on_the_alerts_queue()
    {
        var options = Options();

        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        await channel.QueuePurgeAsync(options.PricesQueue);

        await channel.BasicPublishAsync(
            exchange: options.PricesExchange,
            routingKey: RabbitMqTopology.RoutingKeyFor("IVV"),
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = false },
            body: "{}"u8.ToArray(),
            cancellationToken: CancellationToken.None);

        var delivered = await channel.BasicGetAsync(options.PricesQueue, autoAck: true);

        Assert.NotNull(delivered);
    }

    [Fact]
    public async Task A_rejected_notification_message_reaches_the_dead_letter_queue()
    {
        var options = Options();

        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        await channel.QueuePurgeAsync(options.NotificationsQueue);
        await channel.QueuePurgeAsync(options.NotificationsDeadLetterQueue);

        await channel.BasicPublishAsync(
            exchange: options.AlertsExchange,
            routingKey: RabbitMqTopology.AlertTriggeredRoutingKey,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true },
            body: "not json"u8.ToArray(),
            cancellationToken: CancellationToken.None);

        var delivered = await channel.BasicGetAsync(options.NotificationsQueue, autoAck: false);
        Assert.NotNull(delivered);

        // Reject without requeue — the policy Task 9 applies to a poison message. The
        // dead-letter exchange is what must catch it.
        await channel.BasicNackAsync(delivered!.DeliveryTag, multiple: false, requeue: false);

        var deadLettered = await WaitForDeadLetterAsync(channel, options);

        Assert.NotNull(deadLettered);
    }

    private static async Task<BasicGetResult?> WaitForDeadLetterAsync(
        IChannel channel, RabbitMqOptions options)
    {
        // Dead-lettering is asynchronous inside the broker, so poll rather than assume.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await channel.BasicGetAsync(
                options.NotificationsDeadLetterQueue, autoAck: true);

            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100);
        }

        return null;
    }
}
```

- [ ] **Step 4: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~RabbitMqTopologyTests`
Expected: compile failure — `RabbitMqOptions` and `RabbitMqTopology` do not exist.

- [ ] **Step 5: Write the options**

Create `src/MarketPulse.Application/Configuration/RabbitMqOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace MarketPulse.Application.Configuration;

/// <summary>
/// Lives in Application, not Infrastructure, so both hosts can bind and validate it at
/// startup without either taking a dependency on the broker client. The names are
/// configuration rather than constants because the integration tests and a future
/// multi-environment deployment both need to vary them.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string HostName { get; init; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; init; } = 5672;

    [Required]
    public string UserName { get; init; } = "marketpulse";

    [Required]
    public string Password { get; init; } = "Local!Dev!Pass123";

    [Required]
    public string VirtualHost { get; init; } = "/";

    public string PricesExchange { get; init; } = "marketpulse.prices";
    public string AlertsExchange { get; init; } = "marketpulse.alerts";
    public string AlertsDeadLetterExchange { get; init; } = "marketpulse.alerts.dlx";

    public string PricesQueue { get; init; } = "alerts.prices";
    public string NotificationsQueue { get; init; } = "api.notifications";
    public string NotificationsDeadLetterQueue { get; init; } = "api.notifications.dlq";

    /// <summary>
    /// A worker that was down for a minute must evaluate current prices, not a backlog of
    /// stale ones. Five seconds is comfortably longer than the one-second tick interval.
    /// </summary>
    [Range(100, 600_000)]
    public int TickTtlMilliseconds { get; init; } = 5_000;

    [Range(1, 1_000_000)]
    public int TickQueueMaxLength { get; init; } = 1_000;

    [Range(1, 65535)]
    public ushort PrefetchCount { get; init; } = 100;

    /// <summary>Cap on the connection retry backoff.</summary>
    public TimeSpan MaxConnectionRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
```

- [ ] **Step 6: Write the topology and the contracts**

Create `src/MarketPulse.Infrastructure/Messaging/RabbitMqTopology.cs`:

```csharp
using MarketPulse.Application.Configuration;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Every exchange, queue and binding in the system, declared in one place. Both hosts call
/// this at startup; AMQP declarations are idempotent, so whichever boots first wins and the
/// other is a no-op. Neither process has to wait for the other.
/// </summary>
public static class RabbitMqTopology
{
    public const string AlertTriggeredRoutingKey = "alert.triggered";
    public const string PriceTickRoutingKeyPrefix = "price.tick";

    /// <summary>
    /// The ticker rides in the routing key so a future worker can bind to a subset rather
    /// than filtering everything in process.
    /// </summary>
    public static string RoutingKeyFor(string ticker) =>
        $"{PriceTickRoutingKeyPrefix}.{ticker.Trim().ToUpperInvariant()}";

    public static async Task DeclareAsync(
        IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        // --- Prices: lossy on purpose ------------------------------------------------
        await channel.ExchangeDeclareAsync(
            options.PricesExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: ct);

        await channel.QueueDeclareAsync(
            options.PricesQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                // Stale prices are worse than no prices: they would fire alerts against a
                // market that has already moved on.
                ["x-message-ttl"] = options.TickTtlMilliseconds,
                ["x-max-length"] = options.TickQueueMaxLength,
                ["x-overflow"] = "drop-head"
            },
            cancellationToken: ct);

        await channel.QueueBindAsync(
            options.PricesQueue, options.PricesExchange,
            $"{PriceTickRoutingKeyPrefix}.#", cancellationToken: ct);

        // --- Alerts: durable, confirmed, dead-lettered --------------------------------
        await channel.ExchangeDeclareAsync(
            options.AlertsExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            options.AlertsDeadLetterExchange, ExchangeType.Fanout, durable: true,
            autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(
            options.NotificationsQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = options.AlertsDeadLetterExchange
            },
            cancellationToken: ct);

        await channel.QueueBindAsync(
            options.NotificationsQueue, options.AlertsExchange, AlertTriggeredRoutingKey,
            cancellationToken: ct);

        await channel.QueueDeclareAsync(
            options.NotificationsDeadLetterQueue,
            durable: true, exclusive: false, autoDelete: false, arguments: null,
            cancellationToken: ct);

        // Terminal. Nothing consumes this queue; it is drained by hand when something
        // has gone wrong, which is the point of a dead-letter queue.
        await channel.QueueBindAsync(
            options.NotificationsDeadLetterQueue, options.AlertsDeadLetterExchange,
            routingKey: string.Empty, cancellationToken: ct);
    }
}
```

Create `src/MarketPulse.Infrastructure/Messaging/Contracts/PriceTickMessage.cs`:

```csharp
namespace MarketPulse.Infrastructure.Messaging.Contracts;

/// <summary>
/// The wire format for a tick. Deliberately not <see cref="Domain.ValueObjects.PriceTick"/>:
/// a domain type and a published contract change for different reasons and at different
/// speeds, even when they happen to have the same shape today.
/// </summary>
public sealed record PriceTickMessage(string Ticker, decimal Price, DateTimeOffset TimestampUtc);
```

Create `src/MarketPulse.Infrastructure/Messaging/Contracts/AlertTriggeredMessage.cs`:

```csharp
namespace MarketPulse.Infrastructure.Messaging.Contracts;

/// <summary>
/// Published by the worker's outbox dispatcher, consumed by the API. <see cref="MessageId"/>
/// is the outbox row's id and the consumer's dedupe key — the one field that makes
/// at-least-once delivery safe.
/// </summary>
public sealed record AlertTriggeredMessage(
    Guid MessageId,
    Guid AlertRuleId,
    Guid UserId,
    string Ticker,
    string Direction,
    decimal Threshold,
    decimal TriggeredPrice,
    DateTimeOffset OccurredUtc);
```

- [ ] **Step 7: Write the connection**

Create `src/MarketPulse.Infrastructure/Messaging/RabbitMqConnection.cs`:

```csharp
using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// One lazily-opened, shared connection per process. A broker that is not up yet must not
/// crash-loop the host: the API's alert CRUD is a database operation and keeps working
/// perfectly well while the broker is missing, and the worker's outbox rows simply
/// accumulate until it returns.
/// </summary>
public sealed class RabbitMqConnection(
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RabbitMqOptions _options = options.Value;
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);

        try
        {
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            _connection = await OpenWithBackoffAsync(ct);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken ct)
    {
        var connection = await GetAsync(ct);

        // Confirmation tracking is what lets BasicPublishAsync await the broker's ack. The
        // outbox dispatcher depends on it; the tick sink would only be slowed by it.
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: publisherConfirms,
            publisherConfirmationTrackingEnabled: publisherConfirms);

        return await connection.CreateChannelAsync(channelOptions, ct);
    }

    private async Task<IConnection> OpenWithBackoffAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            UserName = _options.UserName,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,

            // Reconnection after the first successful connect is the client's job, not
            // ours. This only covers the cold start.
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await factory.CreateConnectionAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "RabbitMQ connection failed; retrying in {Delay}.", delay);

                await Task.Delay(delay, ct);

                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2,
                             _options.MaxConnectionRetryDelay.TotalMilliseconds));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~RabbitMqTopologyTests`
Expected: PASS, 3 tests. The first run pulls the `rabbitmq:3-management` image and is slow.

- [ ] **Step 9: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add Directory.Packages.props docker-compose.yml src tests
git commit -m "feat(messaging): add RabbitMQ options, connection and topology declarations"
```

---

### Task 6: Tick fan-out onto the broker

The change that makes ticks visible outside the API process. The trap this task exists to avoid is documented in the spec and repeated in the test.

**Files:**
- Create: `src/MarketPulse.Application/Abstractions/ITickSink.cs`
- Create: `src/MarketPulse.Api/RealTime/SignalRTickSink.cs`
- Create: `src/MarketPulse.Infrastructure/Messaging/RabbitMqTickSink.cs`
- Modify: `src/MarketPulse.Api/RealTime/TickBroadcaster.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Modify: `src/MarketPulse.Api/Program.cs`
- Modify: `src/MarketPulse.Api/appsettings.json`
- Test: `tests/MarketPulse.UnitTests/RealTime/TickBroadcasterTests.cs`

**Interfaces:**
- Consumes: `RabbitMqConnection`, `RabbitMqTopology`, `RabbitMqOptions`, `PriceTickMessage` (Task 5); the existing `PriceTickChannel` and `PriceTick`.
- Produces: `ITickSink` with `Task SendAsync(PriceTick tick, CancellationToken ct)`; `TickBroadcaster(PriceTickChannel, IEnumerable<ITickSink>, ILogger<TickBroadcaster>)`.

- [ ] **Step 1: Write the failing test**

Create `tests/MarketPulse.UnitTests/RealTime/TickBroadcasterTests.cs`:

```csharp
using MarketPulse.Api.RealTime;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketPulse.UnitTests.RealTime;

public class TickBroadcasterTests
{
    private sealed class RecordingSink : ITickSink
    {
        public List<PriceTick> Received { get; } = [];

        public Task SendAsync(PriceTick tick, CancellationToken ct)
        {
            Received.Add(tick);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ITickSink
    {
        public int Calls { get; private set; }

        public Task SendAsync(PriceTick tick, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("broker is down");
        }
    }

    private static PriceTick Tick(string ticker) =>
        new(ticker, 50m, DateTimeOffset.UnixEpoch);

    private static async Task RunAsync(
        PriceTickChannel channel, IEnumerable<ITickSink> sinks, Func<bool> done)
    {
        var broadcaster = new TickBroadcaster(
            channel, sinks, NullLogger<TickBroadcaster>.Instance);

        await broadcaster.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await broadcaster.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Every_sink_receives_every_tick()
    {
        // The bug this test exists to prevent: a Channel<T> DISTRIBUTES items among its
        // readers, it does not broadcast. Two hosted services each reading the channel
        // would split the ticks between them and the dashboard would silently tick at half
        // speed. One reader, many sinks.
        var channel = new PriceTickChannel();
        var first = new RecordingSink();
        var second = new RecordingSink();

        await channel.Writer.WriteAsync(Tick("IVV"));
        await channel.Writer.WriteAsync(Tick("NDQ"));

        await RunAsync(channel, [first, second],
            () => first.Received.Count == 2 && second.Received.Count == 2);

        Assert.Equal(["IVV", "NDQ"], first.Received.Select(t => t.Ticker));
        Assert.Equal(["IVV", "NDQ"], second.Received.Select(t => t.Ticker));
    }

    [Fact]
    public async Task A_failing_sink_does_not_stop_the_others_or_the_reader()
    {
        // The broker being unwell must not degrade the dashboard.
        var channel = new PriceTickChannel();
        var broken = new ThrowingSink();
        var healthy = new RecordingSink();

        await channel.Writer.WriteAsync(Tick("IVV"));
        await channel.Writer.WriteAsync(Tick("NDQ"));

        await RunAsync(channel, [broken, healthy], () => healthy.Received.Count == 2);

        Assert.Equal(2, healthy.Received.Count);
        Assert.Equal(2, broken.Calls);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~TickBroadcasterTests`
Expected: compile failure — `ITickSink` does not exist and `TickBroadcaster`'s constructor takes an `IHubContext`.

- [ ] **Step 3: Write the abstraction and the two sinks**

Create `src/MarketPulse.Application/Abstractions/ITickSink.cs`:

```csharp
using MarketPulse.Domain.ValueObjects;

namespace MarketPulse.Application.Abstractions;

/// <summary>
/// One destination for a price tick. Implementations are expected to be fast and are
/// allowed to fail: <c>TickBroadcaster</c> logs and continues rather than letting one sink
/// take down the others.
/// </summary>
public interface ITickSink
{
    Task SendAsync(PriceTick tick, CancellationToken ct);
}
```

Create `src/MarketPulse.Api/RealTime/SignalRTickSink.cs`:

```csharp
using MarketPulse.Api.Hubs;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.ValueObjects;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.RealTime;

/// <summary>
/// Slice 1's broadcast, unchanged in behaviour and moved behind the sink interface. Prices
/// are public data, so every authenticated client gets the same payload.
/// </summary>
public sealed class SignalRTickSink(IHubContext<PriceHub> hub) : ITickSink
{
    public Task SendAsync(PriceTick tick, CancellationToken ct) =>
        hub.Clients.All.SendAsync(
            "tick",
            new { ticker = tick.Ticker, price = tick.Price, timestampUtc = tick.TimestampUtc },
            ct);
}
```

Create `src/MarketPulse.Infrastructure/Messaging/RabbitMqTickSink.cs`:

```csharp
using System.Text.Json;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.ValueObjects;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Publishes ticks to the prices exchange, fire and forget. No publisher confirms and no
/// outbox: a tick is superseded a second later, so durability here would buy nothing and
/// cost latency on the path that parallels the SignalR broadcast.
/// </summary>
public sealed class RabbitMqTickSink(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options) : ITickSink, IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task SendAsync(PriceTick tick, CancellationToken ct)
    {
        var channel = await ChannelAsync(ct);

        var body = JsonSerializer.SerializeToUtf8Bytes(
            new PriceTickMessage(tick.Ticker, tick.Price, tick.TimestampUtc));

        await channel.BasicPublishAsync(
            exchange: _options.PricesExchange,
            routingKey: RabbitMqTopology.RoutingKeyFor(tick.Ticker),
            mandatory: false,
            basicProperties: new BasicProperties
            {
                Persistent = false,

                // A tick has no originating HTTP request to inherit a correlation id from,
                // so it starts one. That id then rides tick -> worker -> outbox row ->
                // alert event -> API consumer, which is the whole causal chain of one
                // notification in a single searchable value.
                CorrelationId = Guid.NewGuid().ToString()
            },
            body: body,
            cancellationToken: ct);
    }

    private async Task<IChannel> ChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _gate.WaitAsync(ct);

        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            _channel = await connection.CreateChannelAsync(publisherConfirms: false, ct);
            await RabbitMqTopology.DeclareAsync(_channel, _options, ct);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }
}
```

- [ ] **Step 4: Rewrite `TickBroadcaster`**

Replace the body of `src/MarketPulse.Api/RealTime/TickBroadcaster.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.RealTime;

namespace MarketPulse.Api.RealTime;

/// <summary>
/// The single reader of <see cref="PriceTickChannel"/>. It has to be single: a Channel&lt;T&gt;
/// distributes items among its readers rather than broadcasting them, so a second hosted
/// service reading the same channel would silently steal half the ticks. Fan-out happens
/// here, over sinks.
/// </summary>
public sealed class TickBroadcaster(
    PriceTickChannel channel,
    IEnumerable<ITickSink> sinks,
    ILogger<TickBroadcaster> logger) : BackgroundService
{
    private readonly ITickSink[] _sinks = sinks.ToArray();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(stoppingToken))
            {
                foreach (var sink in _sinks)
                {
                    try
                    {
                        await sink.SendAsync(tick, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One sink failing must not stop the others, and must not stop the
                        // reader — a dead reader means the channel fills and drops ticks
                        // for everyone.
                        logger.LogWarning(
                            ex, "Tick sink {Sink} failed for {Ticker}.",
                            sink.GetType().Name, tick.Ticker);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("TickBroadcaster stopping.");
        }
    }
}
```

- [ ] **Step 5: Register the sinks and the options**

In `src/MarketPulse.Infrastructure/DependencyInjection.cs`, add a messaging extension beside `AddInfrastructure`:

```csharp
public static IServiceCollection AddMessaging(this IServiceCollection services)
{
    services.AddSingleton<RabbitMqConnection>();
    services.AddSingleton<ITickSink, RabbitMqTickSink>();
    return services;
}
```

In `src/MarketPulse.Api/Program.cs`, bind and validate the options beside the existing two, and register both sinks:

```csharp
builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

```csharp
builder.Services.AddSingleton<ITickSink, SignalRTickSink>();
builder.Services.AddMessaging();
```

Both go before `builder.Build()`. `AddMessaging` must come after `AddInfrastructure`.

In `src/MarketPulse.Api/appsettings.json`, add the section beside `Jwt`:

```json
  "RabbitMq": {
    "HostName": "localhost",
    "Port": 5672,
    "UserName": "marketpulse",
    "Password": "Local!Dev!Pass123",
    "VirtualHost": "/"
  }
```

- [ ] **Step 6: Run the unit test to verify it passes**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~TickBroadcasterTests`
Expected: PASS, 2 tests.

- [ ] **Step 7: Confirm the existing SignalR test still passes**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~PriceStreamTests`
Expected: PASS, 2 tests. `PriceStreamTests` never sees the broker — `RabbitMqTickSink` fails to connect inside `TestFactory`, logs, and is skipped, which is precisely the degradation the fan-out was built to allow. If this test now fails, the sink's exception is escaping `TickBroadcaster`'s catch.

- [ ] **Step 8: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add src tests
git commit -m "refactor(realtime): fan ticks out over ITickSink and publish them to RabbitMQ"
```

---

### Task 7: The Alerts worker and the evaluation transaction

ADR-001's extraction, performed. The heart of this task is one transaction that writes a state change and an event together.

**Files:**
- Create: `src/MarketPulse.Alerts/MarketPulse.Alerts.csproj`
- Create: `src/MarketPulse.Alerts/Program.cs`
- Create: `src/MarketPulse.Alerts/AlertEvaluator.cs`
- Create: `src/MarketPulse.Alerts/PriceConsumer.cs`
- Create: `src/MarketPulse.Alerts/appsettings.json`
- Create: `src/MarketPulse.Application/Abstractions/IOutbox.cs`
- Create: `src/MarketPulse.Infrastructure/Persistence/Outbox.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Modify: `MarketPulse.sln`
- Modify: `tests/MarketPulse.UnitTests/MarketPulse.UnitTests.csproj`
- Modify: `tests/MarketPulse.UnitTests/Architecture/DependencyRuleTests.cs`
- Test: `tests/MarketPulse.IntegrationTests/AlertEvaluationTests.cs`

**Interfaces:**
- Consumes: `IAlertRuleRepository` (Task 3), `AlertRule` (Task 1), `OutboxMessage` (Task 2), `RabbitMqConnection`, `RabbitMqTopology`, `RabbitMqOptions`, `PriceTickMessage`, `AlertTriggeredMessage` (Task 5).
- Produces: `IOutbox` with `Task EnqueueAsync(Guid messageId, string type, string payload, string? correlationId, DateTimeOffset occurredUtc, CancellationToken ct)`; `AlertEvaluator` with `Task<int> EvaluateAsync(PriceTickMessage tick, string? correlationId, CancellationToken ct)`; `AddPersistence(this IServiceCollection, string)`.

- [ ] **Step 1: Split persistence out of `AddInfrastructure`**

The worker needs the DbContext and the repositories, but must **not** start `FakeTickService` — a second random walk writing ticks nobody reads. In `src/MarketPulse.Infrastructure/DependencyInjection.cs`, split the existing method:

```csharp
/// <summary>
/// Everything both hosts need: the shared database and the repositories over it. The
/// worker takes this; the API takes this plus the tick source and the auth services.
/// </summary>
public static IServiceCollection AddPersistence(
    this IServiceCollection services, string connectionString)
{
    services.AddDbContext<MarketPulseDbContext>(o => o.UseSqlServer(connectionString));
    services.AddScoped<IWatchlistRepository, WatchlistRepository>();
    services.AddScoped<IUserRepository, UserRepository>();
    services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
    services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
    services.AddScoped<INotificationRepository, NotificationRepository>();
    services.AddScoped<IOutbox, Outbox>();
    return services;
}

public static IServiceCollection AddInfrastructure(
    this IServiceCollection services, string connectionString)
{
    services.AddPersistence(connectionString);
    services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
    services.AddSingleton<ITokenService, JwtTokenService>();
    services.AddSingleton<PriceTickChannel>();
    services.AddHostedService<FakeTickService>();
    return services;
}
```

- [ ] **Step 2: Write the outbox abstraction and implementation**

Create `src/MarketPulse.Application/Abstractions/IOutbox.cs`:

```csharp
namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Enqueues an integration event onto the *current* unit of work. It deliberately does not
/// save: the caller's single <c>SaveChangesAsync</c> is what makes the state change and the
/// event atomic, which is the entire point of the pattern. Primitives only, so Application
/// stays ignorant of both the broker and the wire contracts.
/// </summary>
public interface IOutbox
{
    Task EnqueueAsync(
        Guid messageId,
        string type,
        string payload,
        string? correlationId,
        DateTimeOffset occurredUtc,
        CancellationToken ct);
}
```

Create `src/MarketPulse.Infrastructure/Persistence/Outbox.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;

namespace MarketPulse.Infrastructure.Persistence;

/// <summary>
/// Writes through the same scoped <see cref="MarketPulseDbContext"/> the repositories use,
/// so one SaveChangesAsync commits the rule's state change and this row in one transaction.
/// </summary>
public sealed class Outbox(MarketPulseDbContext db) : IOutbox
{
    public async Task EnqueueAsync(
        Guid messageId,
        string type,
        string payload,
        string? correlationId,
        DateTimeOffset occurredUtc,
        CancellationToken ct) =>
        await db.OutboxMessages.AddAsync(
            OutboxMessage.Create(messageId, type, payload, correlationId, occurredUtc), ct);
}
```

- [ ] **Step 3: Write the failing evaluation test**

Create `tests/MarketPulse.IntegrationTests/AlertEvaluationTests.cs`:

```csharp
using MarketPulse.Alerts;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Messaging.Contracts;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(MessagingCollection))]
public class AlertEvaluationTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds the evaluator over a real DbContext, the way the worker's DI does. Scoped
    /// lifetimes matter here: the evaluator, the repository and the outbox must share one
    /// context or the "one transaction" claim is false.
    /// </summary>
    private ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddPersistence(fixture.ConnectionString)
            // Registered against ILogger<T>, not the concrete NullLogger<T>: the evaluator
            // asks for the interface, and DI matches on the exact service type.
            .AddSingleton<ILogger<AlertEvaluator>>(NullLogger<AlertEvaluator>.Instance)
            .AddScoped<AlertEvaluator>()
            .BuildServiceProvider();

    private async Task<Guid> SeedRuleAsync(string ticker, AlertDirection direction, decimal threshold)
    {
        await using var db = fixture.CreateContext();

        var user = User.Register($"eval-{Guid.NewGuid():N}@marketpulse.local", "hash");
        db.Users.Add(user);

        var rule = AlertRule.Create(user.Id, ticker, direction, threshold, Now);
        db.AlertRules.Add(rule);

        await db.SaveChangesAsync();
        return rule.Id;
    }

    [Fact]
    public async Task A_crossing_tick_triggers_the_rule_and_writes_one_outbox_row()
    {
        var ruleId = await SeedRuleAsync("IVV", AlertDirection.Above, 50m);

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();

        var triggered = await evaluator.EvaluateAsync(
            new PriceTickMessage("IVV", 51m, Now), "corr-1", CancellationToken.None);

        Assert.Equal(1, triggered);

        await using var db = fixture.CreateContext();

        var rule = await db.AlertRules.SingleAsync(r => r.Id == ruleId);
        Assert.Equal(AlertRuleStatus.Triggered, rule.Status);
        Assert.Equal(51m, rule.TriggeredPrice);

        var outbox = await db.OutboxMessages
            .Where(m => m.CorrelationId == "corr-1")
            .SingleAsync();

        Assert.Equal(nameof(AlertTriggeredMessage), outbox.Type);
        Assert.Null(outbox.DispatchedUtc);
        Assert.Contains("\"Ticker\":\"IVV\"", outbox.Payload);
    }

    [Fact]
    public async Task A_tick_that_does_not_cross_writes_nothing()
    {
        var ruleId = await SeedRuleAsync("NDQ", AlertDirection.Above, 50m);

        await using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();

        var triggered = await evaluator.EvaluateAsync(
            new PriceTickMessage("NDQ", 49m, Now), "corr-2", CancellationToken.None);

        Assert.Equal(0, triggered);

        await using var db = fixture.CreateContext();
        Assert.Equal(
            AlertRuleStatus.Active,
            (await db.AlertRules.SingleAsync(r => r.Id == ruleId)).Status);
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.CorrelationId == "corr-2"));
    }

    [Fact]
    public async Task A_second_tick_does_not_trigger_an_already_triggered_rule()
    {
        await SeedRuleAsync("VHY", AlertDirection.Above, 50m);

        await using var provider = BuildProvider();

        async Task<int> EvaluateAsync(string correlationId)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<AlertEvaluator>()
                .EvaluateAsync(
                    new PriceTickMessage("VHY", 51m, Now), correlationId, CancellationToken.None);
        }

        Assert.Equal(1, await EvaluateAsync("corr-3a"));

        // One-shot. The price is still above the threshold, which is exactly the case that
        // must not fire again.
        Assert.Equal(0, await EvaluateAsync("corr-3b"));

        await using var db = fixture.CreateContext();
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.CorrelationId == "corr-3b"));
    }

    [Fact]
    public async Task Two_concurrent_evaluations_of_one_rule_produce_one_outbox_row()
    {
        await SeedRuleAsync("FANG", AlertDirection.Below, 50m);

        await using var provider = BuildProvider();

        async Task<int> EvaluateAsync(string correlationId)
        {
            using var scope = provider.CreateScope();
            return await scope.ServiceProvider
                .GetRequiredService<AlertEvaluator>()
                .EvaluateAsync(
                    new PriceTickMessage("FANG", 49m, Now), correlationId, CancellationToken.None);
        }

        // Two worker instances, two ticks, one rule. The RowVersion loser must lose
        // silently rather than double-notifying.
        var results = await Task.WhenAll(EvaluateAsync("corr-4a"), EvaluateAsync("corr-4b"));

        Assert.Equal(1, results.Sum());

        await using var db = fixture.CreateContext();
        Assert.Equal(
            1,
            await db.OutboxMessages.CountAsync(
                m => m.CorrelationId == "corr-4a" || m.CorrelationId == "corr-4b"));
    }
}
```

- [ ] **Step 4: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~AlertEvaluationTests`
Expected: compile failure — the `MarketPulse.Alerts` project does not exist.

- [ ] **Step 5: Create the worker project**

Run:

```bash
dotnet new worker -o src/MarketPulse.Alerts -n MarketPulse.Alerts
dotnet sln add src/MarketPulse.Alerts/MarketPulse.Alerts.csproj
dotnet add src/MarketPulse.Alerts reference src/MarketPulse.Infrastructure
dotnet add tests/MarketPulse.IntegrationTests reference src/MarketPulse.Alerts
dotnet add tests/MarketPulse.UnitTests reference src/MarketPulse.Alerts
```

Delete the generated `src/MarketPulse.Alerts/Worker.cs`. Set the project file to:

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UserSecretsId>marketpulse-alerts</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\MarketPulse.Infrastructure\MarketPulse.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

If `dotnet new worker` added a `Microsoft.Extensions.Hosting` `PackageReference` with an inline
`Version`, move that version to `Directory.Packages.props`.

- [ ] **Step 6: Write the evaluator**

Create `src/MarketPulse.Alerts/AlertEvaluator.cs`:

```csharp
using System.Text.Json;
using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MarketPulse.Alerts;

/// <summary>
/// The one piece of business logic in the worker: given a tick, trigger whatever it crosses
/// and record the event to be published. Everything else here is transport.
/// </summary>
public sealed class AlertEvaluator(
    IAlertRuleRepository rules,
    IOutbox outbox,
    ILogger<AlertEvaluator> logger)
{
    /// <returns>How many rules this tick triggered.</returns>
    public async Task<int> EvaluateAsync(
        PriceTickMessage tick, string? correlationId, CancellationToken ct)
    {
        // Queried per tick rather than cached. Four ticks a second against an index on
        // (Ticker, Status) is nothing; the spec names the in-memory cache as the remedy
        // when a real feed lands in slice 6.
        var candidates = await rules.GetActiveForTickerAsync(tick.Ticker, ct);

        var triggered = 0;

        foreach (var rule in candidates)
        {
            if (!rule.Evaluate(tick.Price))
            {
                continue;
            }

            rule.MarkTriggered(tick.Price, tick.TimestampUtc);

            var messageId = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new AlertTriggeredMessage(
                messageId,
                rule.Id,
                rule.UserId,
                rule.Ticker,
                rule.Direction.ToString(),
                rule.Threshold,
                tick.Price,
                tick.TimestampUtc));

            await outbox.EnqueueAsync(
                messageId, nameof(AlertTriggeredMessage), payload, correlationId,
                tick.TimestampUtc, ct);

            try
            {
                // One SaveChanges, one transaction: the rule's new status and the event
                // that announces it commit together or not at all. Saving per rule rather
                // than per tick keeps one lost race from discarding its siblings' work.
                await rules.SaveChangesAsync(ct);
                triggered++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another worker instance got there first. Expected under horizontal
                // scaling — not an error, and nothing was published.
                logger.LogDebug(
                    "Rule {RuleId} was triggered concurrently; discarding this evaluation.",
                    rule.Id);
                return triggered;
            }
        }

        return triggered;
    }
}
```

> **Implementation note:** on a `DbUpdateConcurrencyException` the scoped `DbContext` is left
> holding a failed change, so the evaluator returns rather than continuing the loop. The
> consumer creates a fresh scope per message, so the next tick starts clean.

- [ ] **Step 7: Write the consumer and the host**

Create `src/MarketPulse.Alerts/PriceConsumer.cs`:

```csharp
using System.Text;
using System.Text.Json;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Alerts;

public sealed class PriceConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<PriceConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(publisherConfirms: false, stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, _options, stoppingToken);

        // Without a prefetch limit the broker pushes the whole queue at us and the TTL
        // stops protecting anything — the messages would already be in our process.
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, stoppingToken);

        await channel.BasicConsumeAsync(
            _options.PricesQueue, autoAck: false, consumer, stoppingToken);

        logger.LogInformation(
            "PriceConsumer listening on {Queue}.", _options.PricesQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });

        await channel.DisposeAsync();
    }

    private async Task HandleAsync(
        IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var correlationId = ea.BasicProperties.CorrelationId;

        try
        {
            var tick = JsonSerializer.Deserialize<PriceTickMessage>(ea.Body.Span);

            if (tick is null)
            {
                logger.LogWarning("Discarding an unreadable tick.");
                await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            using var logScope = logger.BeginScope(
                new Dictionary<string, object?> { ["CorrelationId"] = correlationId });

            await scope.ServiceProvider
                .GetRequiredService<AlertEvaluator>()
                .EvaluateAsync(tick, correlationId, ct);

            // Acked only after the transaction commits. Dying in between redelivers the
            // tick, and the rule is no longer Active, so nothing happens twice.
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to evaluate a tick; discarding it.");

            // Ticks are lossy by design and there is no dead-letter queue on the price
            // path: another tick for this ticker arrives in one second.
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }
}
```

Create `src/MarketPulse.Alerts/Program.cs`:

```csharp
using MarketPulse.Alerts;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MarketPulse")
    ?? throw new InvalidOperationException("ConnectionStrings:MarketPulse is not configured.");

builder.Services.AddOptions<RabbitMqOptions>()
    .Bind(builder.Configuration.GetSection(RabbitMqOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// AddPersistence, not AddInfrastructure: the worker shares the database but must not start
// a second FakeTickService writing ticks nobody reads.
builder.Services.AddPersistence(connectionString);
builder.Services.AddMessaging();

builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddHostedService<PriceConsumer>();

var host = builder.Build();
await host.RunAsync();
```

Create `src/MarketPulse.Alerts/appsettings.json` (replacing the generated one):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "ConnectionStrings": {
    "MarketPulse": "Server=localhost,1433;Database=MarketPulse;User Id=sa;Password=Local!Dev!Pass123;TrustServerCertificate=True"
  },
  "RabbitMq": {
    "HostName": "localhost",
    "Port": 5672,
    "UserName": "marketpulse",
    "Password": "Local!Dev!Pass123",
    "VirtualHost": "/"
  }
}
```

> **Note:** `AddMessaging` registers `RabbitMqTickSink` as an `ITickSink`. Nothing in the
> worker resolves `ITickSink`, so it is never constructed — harmless, and it keeps one
> registration method rather than two that drift apart.

- [ ] **Step 8: Extend the dependency rule test**

In `tests/MarketPulse.UnitTests/Architecture/DependencyRuleTests.cs`, add:

```csharp
/// <summary>
/// The worker is a second deployable, not a second web host. It shares the database and
/// the domain with the API and reaches the outside world only through the broker; a web
/// framework reference would mean someone had started building an HTTP surface on it.
/// </summary>
[Fact]
public void The_alerts_worker_references_no_web_framework()
{
    var worker = typeof(MarketPulse.Alerts.AlertEvaluator).Assembly;

    var forbidden = worker.GetReferencedAssemblies()
        .Select(a => a.Name!)
        .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
        .ToArray();

    Assert.Empty(forbidden);
}
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~AlertEvaluationTests`
Expected: PASS, 4 tests.

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~DependencyRuleTests`
Expected: PASS, 4 tests.

- [ ] **Step 10: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add MarketPulse.sln src tests
git commit -m "feat(alerts): extract the Alerts worker with transactional rule evaluation"
```

---

### Task 8: The outbox dispatcher

Relays outbox rows to the broker and marks them dispatched only once confirmed. Small, and it is the mechanism the README's zero-lost-alerts claim rests on.

**Files:**
- Create: `src/MarketPulse.Application/Abstractions/IEventPublisher.cs`
- Create: `src/MarketPulse.Infrastructure/Messaging/RabbitMqEventPublisher.cs`
- Create: `src/MarketPulse.Alerts/OutboxDispatcher.cs`
- Modify: `src/MarketPulse.Infrastructure/DependencyInjection.cs`
- Modify: `src/MarketPulse.Alerts/Program.cs`
- Test: `tests/MarketPulse.UnitTests/Alerts/OutboxDispatcherTests.cs`
- Test: `tests/MarketPulse.IntegrationTests/OutboxDispatchTests.cs`

**Interfaces:**
- Consumes: `OutboxMessage` (Task 2), `RabbitMqConnection`, `RabbitMqTopology`, `RabbitMqOptions` (Task 5), `MarketPulseDbContext`.
- Produces: `IEventPublisher` with `Task PublishAsync(string routingKey, Guid messageId, string payload, string? correlationId, CancellationToken ct)`; `OutboxDispatcher` (a `BackgroundService`) with `Task<int> DispatchPendingAsync(CancellationToken ct)`.

- [ ] **Step 1: Write the failing unit test**

Create `tests/MarketPulse.UnitTests/Alerts/OutboxDispatcherTests.cs`:

```csharp
using MarketPulse.Alerts;
using MarketPulse.Application.Abstractions;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MarketPulse.UnitTests.Alerts;

public class OutboxDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IAsyncDisposable
    {
        public IEventPublisher Publisher { get; } = Substitute.For<IEventPublisher>();
        public ServiceProvider Provider { get; }
        private readonly SqliteLikeContext _context;

        public Harness()
        {
            _context = new SqliteLikeContext();

            Provider = new ServiceCollection()
                .AddSingleton(_context.Db)
                .AddSingleton(Publisher)
                .BuildServiceProvider();
        }

        public MarketPulseDbContext Db => _context.Db;

        public ValueTask DisposeAsync() => _context.DisposeAsync();
    }

    /// <summary>
    /// An in-memory EF context. This test is about the dispatcher's control flow — publish,
    /// then mark — not about SQL Server. The database-level behaviour is covered by
    /// OutboxDispatchTests against a real broker.
    /// </summary>
    private sealed class SqliteLikeContext : IAsyncDisposable
    {
        public MarketPulseDbContext Db { get; } = new(
            new DbContextOptionsBuilder<MarketPulseDbContext>()
                .UseInMemoryDatabase($"outbox-{Guid.NewGuid():N}")
                .Options);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static OutboxDispatcher Dispatcher(Harness harness) =>
        new(harness.Provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);

    [Fact]
    public async Task A_confirmed_publish_marks_the_row_dispatched()
    {
        await using var harness = new Harness();
        var message = OutboxMessage.Create(Guid.NewGuid(), "AlertTriggered", "{}", "c", Now);
        harness.Db.OutboxMessages.Add(message);
        await harness.Db.SaveChangesAsync();

        var dispatched = await Dispatcher(harness).DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(1, dispatched);
        Assert.NotNull((await harness.Db.OutboxMessages.SingleAsync()).DispatchedUtc);
    }

    [Fact]
    public async Task An_unconfirmed_publish_leaves_the_row_pending_and_counts_the_attempt()
    {
        await using var harness = new Harness();
        harness.Publisher
            .PublishAsync(
                Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("no confirm")));

        harness.Db.OutboxMessages.Add(
            OutboxMessage.Create(Guid.NewGuid(), "AlertTriggered", "{}", "c", Now));
        await harness.Db.SaveChangesAsync();

        var dispatched = await Dispatcher(harness).DispatchPendingAsync(CancellationToken.None);

        // This is the property the whole outbox exists for. If an unconfirmed publish ever
        // marks the row dispatched, the alert is silently lost.
        Assert.Equal(0, dispatched);

        var row = await harness.Db.OutboxMessages.SingleAsync();
        Assert.Null(row.DispatchedUtc);
        Assert.Equal(1, row.AttemptCount);
    }

    [Fact]
    public async Task An_already_dispatched_row_is_not_published_again()
    {
        await using var harness = new Harness();
        var message = OutboxMessage.Create(Guid.NewGuid(), "AlertTriggered", "{}", "c", Now);
        message.MarkDispatched(Now);
        harness.Db.OutboxMessages.Add(message);
        await harness.Db.SaveChangesAsync();

        var dispatched = await Dispatcher(harness).DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(0, dispatched);
        await harness.Publisher.DidNotReceive().PublishAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }
}
```

Add the in-memory provider so this test can run without a container:

```bash
dotnet add tests/MarketPulse.UnitTests package Microsoft.EntityFrameworkCore.InMemory
```

Move the version into `Directory.Packages.props` if `dotnet add` put it in the `.csproj`.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~OutboxDispatcherTests`
Expected: compile failure — `IEventPublisher` and `OutboxDispatcher` do not exist.

- [ ] **Step 3: Write the publisher abstraction and implementation**

Create `src/MarketPulse.Application/Abstractions/IEventPublisher.cs`:

```csharp
namespace MarketPulse.Application.Abstractions;

/// <summary>
/// Publishes one integration event and returns only once the broker has confirmed it.
/// A faulted task means "not confirmed" and must leave the outbox row pending.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(
        string routingKey,
        Guid messageId,
        string payload,
        string? correlationId,
        CancellationToken ct);
}
```

Create `src/MarketPulse.Infrastructure/Messaging/RabbitMqEventPublisher.cs`:

```csharp
using System.Text;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.Infrastructure.Messaging;

/// <summary>
/// Publishes to the alerts exchange on a confirming channel. With publisher confirmation
/// tracking enabled, BasicPublishAsync does not complete until the broker acks — so an
/// awaited call that returns without throwing is a genuine durability guarantee, and a
/// throw is the dispatcher's signal to leave the row pending.
/// </summary>
public sealed class RabbitMqEventPublisher(
    RabbitMqConnection connection,
    IOptions<RabbitMqOptions> options) : IEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(
        string routingKey,
        Guid messageId,
        string payload,
        string? correlationId,
        CancellationToken ct)
    {
        var channel = await ChannelAsync(ct);

        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = messageId.ToString(),
            CorrelationId = correlationId,
            ContentType = "application/json"
        };

        await channel.BasicPublishAsync(
            exchange: _options.AlertsExchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payload),
            cancellationToken: ct);
    }

    private async Task<IChannel> ChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _gate.WaitAsync(ct);

        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            _channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
            await RabbitMqTopology.DeclareAsync(_channel, _options, ct);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }
}
```

Register it in `AddMessaging`:

```csharp
services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
```

- [ ] **Step 4: Write the dispatcher**

Create `src/MarketPulse.Alerts/OutboxDispatcher.cs`:

```csharp
using MarketPulse.Application.Abstractions;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using MarketPulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MarketPulse.Alerts;

/// <summary>
/// Relays pending outbox rows to the broker. Polls rather than listens: a dispatcher that
/// missed a notification signal would strand the row until something else happened to wake
/// it, whereas a poll loop is self-healing by construction. Half a second of latency on a
/// path that is already eventually consistent is not worth engineering away.
/// </summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await DispatchPendingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The broker or the database is unwell. Rows stay pending and the next
                    // tick tries again — this loop must never be the thing that stops.
                    logger.LogWarning(ex, "Outbox dispatch pass failed; retrying.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("OutboxDispatcher stopping.");
        }
    }

    /// <returns>How many rows were confirmed and marked dispatched.</returns>
    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketPulseDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var pending = await db.OutboxMessages
            .Where(m => m.DispatchedUtc == null)
            .OrderBy(m => m.OccurredUtc)
            .Take(BatchSize)
            .ToListAsync(ct);

        var dispatched = 0;

        foreach (var message in pending)
        {
            try
            {
                await publisher.PublishAsync(
                    RabbitMqTopology.AlertTriggeredRoutingKey,
                    message.Id,
                    message.Payload,
                    message.CorrelationId,
                    ct);

                // Only now. A publish that was never confirmed must leave this row alone
                // so the next pass retries it — at-least-once, which is why the consumer
                // dedupes on message id.
                message.MarkDispatched(DateTimeOffset.UtcNow);
                dispatched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.RecordAttempt();
                logger.LogWarning(
                    ex, "Publishing outbox message {MessageId} failed (attempt {Attempt}).",
                    message.Id, message.AttemptCount);
            }
        }

        await db.SaveChangesAsync(ct);
        return dispatched;
    }
}
```

Register it in `src/MarketPulse.Alerts/Program.cs`, beside `PriceConsumer`:

```csharp
builder.Services.AddHostedService<OutboxDispatcher>();
```

- [ ] **Step 5: Write the integration test**

Create `tests/MarketPulse.IntegrationTests/OutboxDispatchTests.cs`:

```csharp
using MarketPulse.Alerts;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure;
using MarketPulse.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(MessagingCollection))]
public class OutboxDispatchTests(SqlServerFixture sql, RabbitMqFixture rabbit)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private ServiceProvider BuildProvider()
    {
        var uri = new Uri(rabbit.ConnectionString);

        var options = new RabbitMqOptions
        {
            HostName = uri.Host,
            Port = uri.Port,
            UserName = uri.UserInfo.Split(':')[0],
            Password = uri.UserInfo.Split(':')[1]
        };

        return new ServiceCollection()
            .AddPersistence(sql.ConnectionString)
            .AddSingleton<IOptions<RabbitMqOptions>>(Options.Create(options))
            .AddLogging()
            .AddMessaging()
            .BuildServiceProvider();
    }

    [Fact]
    public async Task A_pending_row_is_published_and_marked_dispatched()
    {
        var messageId = Guid.NewGuid();

        await using (var db = sql.CreateContext())
        {
            db.OutboxMessages.Add(OutboxMessage.Create(
                messageId, "AlertTriggeredMessage", """{"Ticker":"IVV"}""", "corr-out", Now));
            await db.SaveChangesAsync();
        }

        await using var provider = BuildProvider();
        var options = provider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        // Drain anything a previous test left behind so the assertion below is about us.
        await using (var connection = await rabbit.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
            await channel.QueuePurgeAsync(options.NotificationsQueue);
        }

        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);

        var dispatched = await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.True(dispatched >= 1);

        await using (var db = sql.CreateContext())
        {
            Assert.NotNull(
                (await db.OutboxMessages.SingleAsync(m => m.Id == messageId)).DispatchedUtc);
        }

        await using (var connection = await rabbit.ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            var delivered = await channel.BasicGetAsync(
                options.NotificationsQueue, autoAck: true);

            Assert.NotNull(delivered);
            Assert.Equal("corr-out", delivered!.BasicProperties.CorrelationId);
        }
    }
}
```

- [ ] **Step 6: Run both tests to verify they pass**

Run: `dotnet test tests/MarketPulse.UnitTests --filter FullyQualifiedName~OutboxDispatcherTests`
Expected: PASS, 3 tests.

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~OutboxDispatchTests`
Expected: PASS, 1 test.

- [ ] **Step 7: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add Directory.Packages.props src tests
git commit -m "feat(alerts): relay outbox rows to RabbitMQ behind publisher confirms"
```

---

### Task 9: The idempotent consumer and per-user delivery

The last link. This task closes the loop and carries the end-to-end test that proves the whole pipeline.

**Files:**
- Create: `src/MarketPulse.Api/Hubs/NotificationHub.cs`
- Create: `src/MarketPulse.Api/Messaging/AlertTriggeredConsumer.cs`
- Modify: `src/MarketPulse.Api/Program.cs`
- Test: `tests/MarketPulse.IntegrationTests/AlertPipelineTests.cs`

**Interfaces:**
- Consumes: `Notification` (Task 2), `INotificationRepository` (Task 4), `RabbitMqConnection`, `RabbitMqTopology`, `RabbitMqOptions`, `AlertTriggeredMessage` (Task 5).
- Produces: `NotificationHub` mapped at `/hubs/notifications`, pushing the client method `"notification"` with a payload of `{ id, alertRuleId, ticker, direction, threshold, triggeredPrice, occurredUtc }`.

- [ ] **Step 1: Write the hub**

Create `src/MarketPulse.Api/Hubs/NotificationHub.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MarketPulse.Api.Hubs;

/// <summary>
/// Private, per-user delivery — the counterpart to <see cref="PriceHub"/>, which broadcasts
/// public data to everyone. Two data classifications, two hubs, so the distinction is
/// structural rather than a convention someone has to remember.
///
/// No group management is needed: SignalR's default IUserIdProvider reads
/// ClaimTypes.NameIdentifier, which is the same claim CurrentUser reads and the same claim
/// the JWT's `sub` is mapped to. Clients.User(userId) therefore already means "this user's
/// connections, wherever they are".
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub;
```

- [ ] **Step 2: Write the consumer**

Create `src/MarketPulse.Api/Messaging/AlertTriggeredConsumer.cs`:

```csharp
using System.Text.Json;
using MarketPulse.Api.Hubs;
using MarketPulse.Application.Abstractions;
using MarketPulse.Application.Configuration;
using MarketPulse.Domain.Entities;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.Api.Messaging;

public sealed class AlertTriggeredConsumer(
    RabbitMqConnection connection,
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationHub> hub,
    IOptions<RabbitMqOptions> options,
    ILogger<AlertTriggeredConsumer> logger) : BackgroundService
{
    private readonly RabbitMqOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(publisherConfirms: false, stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, _options, stoppingToken);
        await channel.BasicQosAsync(0, _options.PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, stoppingToken);

        await channel.BasicConsumeAsync(
            _options.NotificationsQueue, autoAck: false, consumer, stoppingToken);

        logger.LogInformation(
            "AlertTriggeredConsumer listening on {Queue}.", _options.NotificationsQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });

        await channel.DisposeAsync();
    }

    private async Task HandleAsync(
        IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        AlertTriggeredMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<AlertTriggeredMessage>(ea.Body.Span);
        }
        catch (JsonException ex)
        {
            // Permanent. Requeueing an unparseable message loops forever.
            logger.LogError(ex, "Unparseable alert message; dead-lettering.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        if (message is null)
        {
            logger.LogError("Null alert message; dead-lettering.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = ea.BasicProperties.CorrelationId,
            ["MessageId"] = message.MessageId
        });

        try
        {
            var notification = await PersistAsync(message, ct);

            if (notification is not null)
            {
                await PushAsync(message, notification, ct);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (DbUpdateException ex)
        {
            // The database is unreachable or otherwise unhappy — the alert is real and the
            // fault is ours, so requeue. Dead-lettering here would lose exactly what the
            // outbox exists to protect. This can loop while SQL Server is down; that is
            // deliberate, and the redelivery counter that would bound it is named in the
            // spec as observability-slice work.
            logger.LogWarning(ex, "Transient failure persisting a notification; requeueing.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Permanent failure handling an alert; dead-lettering.");
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }

    /// <returns>The new notification, or null if this message was already handled.</returns>
    private async Task<Notification?> PersistAsync(
        AlertTriggeredMessage message, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var notification = Notification.Create(
            message.MessageId,
            message.UserId,
            message.AlertRuleId,
            message.Ticker,
            Enum.Parse<AlertDirection>(message.Direction),
            message.Threshold,
            message.TriggeredPrice,
            message.OccurredUtc,
            DateTimeOffset.UtcNow);

        await repo.AddAsync(notification, ct);

        try
        {
            await repo.SaveChangesAsync(ct);
            return notification;
        }
        catch (DbUpdateException ex) when (IsDuplicateMessageId(ex))
        {
            // Insert-and-catch, not read-then-write: the unique index on MessageId is the
            // dedupe, and a read-then-write would race itself under two API instances.
            logger.LogDebug("Duplicate delivery of {MessageId}; already stored.", message.MessageId);
            return null;
        }
    }

    private Task PushAsync(
        AlertTriggeredMessage message, Notification notification, CancellationToken ct) =>
        // The row is the guarantee; this push is the optimisation. A user who is offline
        // sees it on next load, which is why delivery failure here is not fatal.
        hub.Clients.User(message.UserId.ToString()).SendAsync(
            "notification",
            new
            {
                id = notification.Id,
                alertRuleId = message.AlertRuleId,
                ticker = message.Ticker,
                direction = message.Direction,
                threshold = message.Threshold,
                triggeredPrice = message.TriggeredPrice,
                occurredUtc = message.OccurredUtc
            },
            ct);

    /// <summary>
    /// SQL Server raises 2601 for a unique-index violation and 2627 for a unique-constraint
    /// one. Matching on the number rather than the message keeps this working under any
    /// server locale.
    /// </summary>
    private static bool IsDuplicateMessageId(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
```

> **Layering note:** this file references `Microsoft.Data.SqlClient`, which the API may
> reference (only `Application` is forbidden from doing so — see `DependencyRuleTests`). It
> arrives transitively through Infrastructure, so no package reference is needed.

- [ ] **Step 3: Wire the hub and the consumer into the host**

In `src/MarketPulse.Api/Program.cs`:

```csharp
builder.Services.AddHostedService<AlertTriggeredConsumer>();
```

and beside the existing hub mapping:

```csharp
app.MapHub<NotificationHub>("/hubs/notifications");
```

- [ ] **Step 4: Write the end-to-end test**

Create `tests/MarketPulse.IntegrationTests/AlertPipelineTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.Messaging;
using MarketPulse.Infrastructure.Messaging.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// The slice's proof. An alert rule created over HTTP, a tick published to the broker, and a
/// notification arriving on a websocket — through the real worker code, the real outbox, and
/// a real RabbitMQ.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class AlertPipelineTests(SqlServerFixture sql, RabbitMqFixture rabbit)
{
    private sealed record NotificationResponse(Guid Id, string Ticker, decimal TriggeredPrice);

    private sealed record NotificationPush(Guid Id, string Ticker, decimal TriggeredPrice);

    private Dictionary<string, string?> BrokerSettings()
    {
        var uri = new Uri(rabbit.ConnectionString);

        return new Dictionary<string, string?>
        {
            ["RabbitMq:HostName"] = uri.Host,
            ["RabbitMq:Port"] = uri.Port.ToString(),
            ["RabbitMq:UserName"] = uri.UserInfo.Split(':')[0],
            ["RabbitMq:Password"] = uri.UserInfo.Split(':')[1]
        };
    }

    private static async Task PublishAlertAsync(
        RabbitMqFixture rabbit, RabbitMqOptions options, AlertTriggeredMessage message)
    {
        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);

        await channel.BasicPublishAsync(
            exchange: options.AlertsExchange,
            routingKey: RabbitMqTopology.AlertTriggeredRoutingKey,
            mandatory: false,
            basicProperties: new BasicProperties
            {
                Persistent = true,
                MessageId = message.MessageId.ToString(),
                CorrelationId = "pipeline-test"
            },
            body: JsonSerializer.SerializeToUtf8Bytes(message),
            cancellationToken: CancellationToken.None);
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> probe, int attempts = 50)
        where T : class
    {
        for (var i = 0; i < attempts; i++)
        {
            var result = await probe();

            if (result is not null)
            {
                return result;
            }

            await Task.Delay(100);
        }

        return null;
    }

    [Fact]
    public async Task An_alert_message_becomes_a_notification_and_reaches_only_its_owner()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());

        var aliceEmail = AuthenticatedClient.NewEmail();
        var alice = await AuthenticatedClient.RegisterAsync(factory, aliceEmail);
        var bob = await AuthenticatedClient.RegisterAsync(factory);

        Guid aliceId;
        await using (var db = sql.CreateContext())
        {
            aliceId = await db.Users.Where(u => u.Email == aliceEmail)
                                    .Select(u => u.Id).SingleAsync();
        }

        var options = factory.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        var accessCookie = AuthenticatedClient.ReadCookie(
            await factory.CreateClient().PostAsJsonAsync(
                "/api/v1/auth/login",
                new { Email = aliceEmail, Password = AuthenticatedClient.ValidPassword }),
            "mp_access");

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/notifications", o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = $"mp_access={accessCookie}";
            })
            .Build();

        var pushed = new TaskCompletionSource<NotificationPush>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<NotificationPush>("notification", p => pushed.TrySetResult(p));
        await connection.StartAsync();

        var message = new AlertTriggeredMessage(
            Guid.NewGuid(), Guid.NewGuid(), aliceId, "IVV",
            "Above", 50m, 51.5m, DateTimeOffset.UtcNow);

        await PublishAlertAsync(rabbit, options, message);

        var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        await connection.DisposeAsync();

        Assert.Same(pushed.Task, completed);
        Assert.Equal("IVV", (await pushed.Task).Ticker);

        // Persisted, and visible over HTTP.
        var stored = await WaitForAsync(async () =>
        {
            var list = await alice.GetFromJsonAsync<List<NotificationResponse>>(
                "/api/v1/notifications");
            return list?.Count > 0 ? list : null;
        });

        Assert.NotNull(stored);
        Assert.Equal(51.5m, stored![0].TriggeredPrice);

        // And Bob sees nothing. Prices are public; notifications are not.
        Assert.Empty((await bob.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications"))!);
    }

    [Fact]
    public async Task Redelivering_the_same_message_id_yields_exactly_one_notification()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());

        var email = AuthenticatedClient.NewEmail();
        var client = await AuthenticatedClient.RegisterAsync(factory, email);

        Guid userId;
        await using (var db = sql.CreateContext())
        {
            userId = await db.Users.Where(u => u.Email == email)
                                   .Select(u => u.Id).SingleAsync();
        }

        var options = factory.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        var message = new AlertTriggeredMessage(
            Guid.NewGuid(), Guid.NewGuid(), userId, "NDQ",
            "Below", 40m, 39m, DateTimeOffset.UtcNow);

        // The same message twice, exactly as the outbox would republish after a lost confirm.
        await PublishAlertAsync(rabbit, options, message);
        await PublishAlertAsync(rabbit, options, message);

        var stored = await WaitForAsync(async () =>
        {
            var list = await client.GetFromJsonAsync<List<NotificationResponse>>(
                "/api/v1/notifications");
            return list?.Count > 0 ? list : null;
        });

        Assert.NotNull(stored);

        // Give the second delivery time to be wrongly inserted, so this test can fail.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var final = await client.GetFromJsonAsync<List<NotificationResponse>>(
            "/api/v1/notifications");

        Assert.Single(final!);
    }

    [Fact]
    public async Task An_unparseable_message_reaches_the_dead_letter_queue()
    {
        await using var factory = TestFactory.Create(sql, BrokerSettings());
        _ = await AuthenticatedClient.RegisterAsync(factory);

        var options = factory.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        await using var connection = await rabbit.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, options, CancellationToken.None);
        await channel.QueuePurgeAsync(options.NotificationsDeadLetterQueue);

        await channel.BasicPublishAsync(
            exchange: options.AlertsExchange,
            routingKey: RabbitMqTopology.AlertTriggeredRoutingKey,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true },
            body: Encoding.UTF8.GetBytes("this is not json"),
            cancellationToken: CancellationToken.None);

        var deadLettered = await WaitForAsync<object>(async () =>
            await channel.BasicGetAsync(options.NotificationsDeadLetterQueue, autoAck: true));

        // One bad message must not block the queue behind it.
        Assert.NotNull(deadLettered);
    }
}
```

`TestFactory.Create` already accepts an overrides dictionary, so `BrokerSettings()` needs no
change to it.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MarketPulse.IntegrationTests --filter FullyQualifiedName~AlertPipelineTests`
Expected: PASS, 3 tests.

If the SignalR assertion times out, check that `MapHub<NotificationHub>` runs after
`UseAuthentication`, and that the login response actually set `mp_access` — the test reads it
by name.

- [ ] **Step 6: Run the whole suite and commit**

Run: `dotnet test`

```bash
git add src tests
git commit -m "feat(notifications): consume alert events idempotently and deliver per user"
```

---

### Task 10: ADR-009 and the documentation the slice owes

Four documents claim things that this slice either made true or proved wrong. A slice is not finished until they agree with the code.

**Files:**
- Create: `docs/adr/009-messaging-architecture.md`
- Modify: `README.md`
- Modify: `docs/TESTING.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Write ADR-009**

Create `docs/adr/009-messaging-architecture.md`, following the structure of the existing ADRs
(Context · Decision · Rationale · Rejected alternatives · Consequences). It must cover, each
with the reasoning from the spec's decision table:

1. **Shared database, extracted compute.** The worker reads and writes the API's SQL Server.
   Say plainly that this is coupling, that a schema change to `AlertRules` is a coordinated
   deploy of two services, and that what ADR-001 justified was independent *scaling* of
   evaluation, not independent deployment of a data model. Rejected: a worker-owned schema fed
   by replication events.
2. **The outbox sits on `AlertTriggered`, not on price ticks** — correcting the README's
   original diagram. A tick is superseded a second later; the real atomicity problem is that
   "rule marked fired" and "notification event published" must not diverge.
3. **Raw `RabbitMQ.Client` over MassTransit.** The topology, the ack policy and the dedupe are
   the substance; a library would have configured them.
4. **One-shot trigger semantics.** Rejected: crossing detection (needs restart-fragile
   last-price state) and cooldown windows (make "did it fire?" time-dependent).
5. **A separate `NotificationHub`.** Prices are public broadcast, notifications are private
   per-user — two data classifications, two hubs. The cost is a second websocket per tab.
6. **`RowVersion` optimistic concurrency**, which is what makes two worker instances a
   supported configuration rather than a double-notification bug.

Under Consequences, record the four limitations the spec names: rules queried per tick, a rule
that can fire the instant it is created, at-least-once delivery deduped at exactly one point,
and the requeue loop that has no bound until the observability slice adds one.

- [ ] **Step 2: Correct the README**

Three changes:

1. **The architecture diagram** (around line 60) routes `Outbox` → `publish PriceTick`. Redraw
   it so ticks go straight to RabbitMQ and the outbox sits between the Alerts worker and the
   notification path. This is the claim the spec deliberately contradicts, and leaving it would
   make the diagram wrong about the code.
2. **The "not yet built" caveat** (around line 363) says the Alerts worker and RabbitMQ do not
   exist. They do now — narrow the caveat to what is still missing rather than deleting it.
3. **Category 11's bullet list** (around line 263) should stop describing the chaos test as
   present. Mark it as slice 4b, since it is the one item in that list this slice does not
   deliver.

Do not touch the idempotency-keys claim (line 226) — that is slice 5, and the roadmap's
register already tracks it.

- [ ] **Step 3: Update `docs/TESTING.md`**

Add the new coverage: Testcontainers now starts RabbitMQ beside SQL Server; the alert
evaluation engine is the TDD example the strategy section refers to; cross-user isolation now
covers alert rules and notifications. Under whatever "deliberately not tested" section exists,
add the four exclusions from the spec: no chaos test (4b), no evaluation-throughput load test
(slice 12), no SignalR transport test, no multi-instance worker test.

- [ ] **Step 4: Update the roadmap**

In `docs/ROADMAP.md`:

- Move phase 3's row from **Not started** to **Partial**: present is RabbitMQ, the outbox, the
  Alerts worker, alert rules, notifications and per-user delivery; absent is the alerts UI and
  the chaos test.
- Add slice 4a to the **Completed slices** table, dated, with a one-line summary.
- Remove 4a from **Remaining slices**, and update the count in the sentence above it — it says
  "Eleven slices remain".
- In 4b's entry, keep the note that the README's headline claim is not substantiated until it
  lands. That is still true: this slice built the outbox, it did not prove it under failure.

- [ ] **Step 5: Verify the docs against the code**

Run: `dotnet test`
Expected: PASS, everything.

Then re-read the spec's **Done criteria** and walk each box. The manual ones need a real
`docker compose up`, the API, and `dotnet run --project src/MarketPulse.Alerts`:

```bash
docker compose up -d
dotnet ef database update --project src/MarketPulse.Infrastructure --startup-project src/MarketPulse.Api
dotnet run --project src/MarketPulse.Api &
dotnet run --project src/MarketPulse.Alerts &
```

Create an alert just below the current price of a seeded ticker and wait for the random walk
to cross it. Then stop the broker (`docker compose stop rabbitmq`), confirm alert CRUD and the
dashboard's ticks both keep working, restart it, and confirm the accumulated outbox rows flush.
That last pair is the manual rehearsal of what 4b's chaos test will automate.

- [ ] **Step 6: Commit**

```bash
git add docs README.md
git commit -m "docs: add ADR-009 and reconcile the README, testing notes and roadmap with slice 4a"
```

---

## Done

All ten tasks complete means the spec's done criteria are met and the branch is ready for
`superpowers:finishing-a-development-branch`. Per this repo's flow, that merges into `test`,
not `main`.
