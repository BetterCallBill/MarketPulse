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

        // A fresh account per test class run: an empty watchlist with no seeded items,
        // and no cross-test interference through the shared dev user.
        _client = await AuthenticatedClient.RegisterAsync(_factory);
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
