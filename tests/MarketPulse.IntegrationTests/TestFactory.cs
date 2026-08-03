using MarketPulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// Builds the API against the Testcontainers SQL Server instead of the developer database.
/// </summary>
public static class TestFactory
{
    /// <summary>
    /// TestServer never populates <c>Connection.RemoteIpAddress</c>, so every request made
    /// through a given factory — from every client it hands out — falls into the rate
    /// limiter's single "unknown" partition. The production default of 10 auth requests a
    /// minute therefore behaves as a budget for a whole test class rather than per caller,
    /// and a class that grew past it would fail on whichever test happened to run last.
    ///
    /// Tests that are not about throttling get a limit high enough to stay out of the way.
    /// The ones that are pass their own value in <paramref name="overrides"/>, which wins.
    /// </summary>
    private const string UnthrottledAuthRequestsPerMinute = "1000";

    public static WebApplicationFactory<Program> Create(
        SqlServerFixture fixture, Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:LoginRequestsPerMinute"] = UnthrottledAuthRequestsPerMinute
        };

        foreach (var (key, value) in overrides ?? [])
        {
            settings[key] = value;
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Development);
            b.ConfigureAppConfiguration(c => c.AddInMemoryCollection(settings));
            b.ConfigureServices(services =>
            {
                var descriptor = services.Single(
                    d => d.ServiceType == typeof(DbContextOptions<MarketPulseDbContext>));
                services.Remove(descriptor);
                services.AddDbContext<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });
    }
}
