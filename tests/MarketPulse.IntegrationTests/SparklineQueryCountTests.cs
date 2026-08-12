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
