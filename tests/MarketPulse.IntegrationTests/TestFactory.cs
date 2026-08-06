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
            // The Dapper connection factory is built from the connection string Program.cs reads
            // off configuration — unlike the DbContextOptions replacement below, it cannot be
            // swapped after the fact, so the configuration itself must point at the container.
            ["ConnectionStrings:MarketPulse"] = fixture.ConnectionString,
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

                // Singleton, not the AddDbContext default of Scoped: AddPersistence also
                // registers IDbContextFactory<MarketPulseDbContext> (itself a singleton, for
                // IdempotencyStore's own-context-per-call seam) which depends on this same
                // options service. Re-adding it here as Scoped — which plain AddDbContext
                // would do — would leave that singleton factory depending on a scoped
                // service, and ASP.NET Core's startup validation rejects that captive
                // dependency before a single request runs.
                services.AddDbContextFactory<MarketPulseDbContext>(
                    o => o.UseSqlServer(fixture.ConnectionString));
            });
        });
    }
}
