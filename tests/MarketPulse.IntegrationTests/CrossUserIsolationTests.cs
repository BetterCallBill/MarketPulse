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
public class CrossUserIsolationTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;

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

        _alice = await AuthenticatedClient.RegisterAsync(_factory);
        _bob = await AuthenticatedClient.RegisterAsync(_factory);
    }

    [Fact]
    public async Task Bob_cannot_see_what_Alice_added()
    {
        await _alice.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("IVV"));

        var bobsList = await _bob.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");

        Assert.NotNull(bobsList);
        Assert.DoesNotContain(bobsList!.Items, i => i.Ticker == "IVV");
    }

    [Fact]
    public async Task Alice_and_Bob_have_different_watchlists()
    {
        var alices = await _alice.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");
        var bobs = await _bob.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");

        Assert.NotEqual(alices!.Id, bobs!.Id);
    }

    [Fact]
    public async Task Bob_adding_the_same_ticker_is_not_a_duplicate()
    {
        // The uniqueness rule is per watchlist, not global. If this 409s, the query is
        // reaching across users.
        await _alice.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("NDQ"));

        var bobsAdd = await _bob.PostAsJsonAsync(
            "/api/v1/watchlist/items", new AddWatchlistItemCommand("NDQ"));

        bobsAdd.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Bob_deleting_a_ticker_does_not_remove_it_from_Alices_list()
    {
        await _alice.PostAsJsonAsync("/api/v1/watchlist/items", new AddWatchlistItemCommand("VAS"));

        var bobsDelete = await _bob.DeleteAsync("/api/v1/watchlist/items/VAS");

        // Bob has no such item: a 4xx is correct, silent success is not.
        Assert.False(bobsDelete.IsSuccessStatusCode);

        var alicesList = await _alice.GetFromJsonAsync<WatchlistDto>("/api/v1/watchlist");
        Assert.Contains(alicesList!.Items, i => i.Ticker == "VAS");
    }

    [Fact]
    public async Task A_logged_out_session_cannot_read_the_watchlist()
    {
        await _alice.PostAsync("/api/v1/auth/logout", content: null);

        var response = await _alice.GetAsync("/api/v1/watchlist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public Task DisposeAsync()
    {
        _alice.Dispose();
        _bob.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }
}
