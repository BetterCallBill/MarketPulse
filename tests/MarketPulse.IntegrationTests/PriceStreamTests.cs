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
        await using var factory = TestFactory.Create(fixture);

        var accessCookie = await AuthenticatedClient.RegisterAndGetAccessCookieAsync(factory);

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/prices", o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = accessCookie;
            })
            .Build();

        var received = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<TickPayload>("tick", payload => received.TrySetResult(payload.Ticker));

        await connection.StartAsync();

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        await connection.DisposeAsync();

        Assert.Same(received.Task, completed);
        Assert.False(string.IsNullOrWhiteSpace(await received.Task));
    }

    [Fact]
    public async Task An_unauthenticated_client_cannot_connect_to_the_hub()
    {
        await using var factory = TestFactory.Create(fixture);

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

    private sealed record TickPayload(string Ticker, decimal Price, DateTimeOffset TimestampUtc);
}
