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
