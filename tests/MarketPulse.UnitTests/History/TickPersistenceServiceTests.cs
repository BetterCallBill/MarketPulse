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
            Arg.Is<IReadOnlyList<PriceTick>>(b => b!.Count == 2), Arg.Any<CancellationToken>());
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

        // BackgroundService.StartAsync dispatches ExecuteAsync via Task.Run and returns
        // immediately, without waiting for it to actually begin. Under heavy thread-pool
        // contention that Task.Run can still be queued when StopAsync cancels the linked
        // token below — and Task.Run's own CancellationToken parameter then skips invoking
        // ExecuteAsync altogether, so the shutdown flush never happens. Give the loop a
        // moment to actually start and park on the timer wait before writing and stopping;
        // this is scheduling slack, not a change to what the test is verifying.
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        buffer.Writer.TryWrite(Tick(7m));

        await service.StopAsync(CancellationToken.None);

        await writer.Received().WriteAsync(
            Arg.Is<IReadOnlyList<PriceTick>>(b => b!.Count == 1), Arg.Any<CancellationToken>());
    }
}
