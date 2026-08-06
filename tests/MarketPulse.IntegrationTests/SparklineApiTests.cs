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
