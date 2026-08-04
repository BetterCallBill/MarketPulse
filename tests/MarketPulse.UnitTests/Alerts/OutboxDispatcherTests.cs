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
