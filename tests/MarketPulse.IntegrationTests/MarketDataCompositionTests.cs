using MarketPulse.Application.Configuration;
using MarketPulse.Infrastructure.RealTime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MarketPulse.IntegrationTests;

/// <summary>
/// Task 4's composition switch: exactly one tick producer is hosted, chosen by
/// MarketData:Source, and a bad value fails startup validation instead of silently falling
/// back to the fake. None of these tests await a poll — the Yahoo case pins PollInterval to
/// an hour so nothing fires against the real BaseUrl inside the TestServer process while the
/// test is running.
/// </summary>
[Collection(nameof(SqlServerCollection))]
public class MarketDataCompositionTests(SqlServerFixture fixture)
{
    [Fact]
    public void Default_configuration_hosts_the_fake_tick_service()
    {
        using var factory = TestFactory.Create(fixture);

        var hosted = factory.Services.GetServices<IHostedService>();

        Assert.Contains(hosted, s => s is FakeTickService);
        Assert.DoesNotContain(hosted, s => s is YahooPriceFeedService);
    }

    [Fact]
    public void Yahoo_source_hosts_the_real_price_feed_instead()
    {
        // AddInfrastructure reads MarketData:Source straight off IConfiguration before the
        // host finishes building — deliberately, so the switch doesn't depend on options
        // validation having run yet (see its doc comment). TestFactory's usual override
        // path (ConfigureAppConfiguration, added via WithWebHostBuilder) only lands in the
        // final merged configuration once the host is built, which is too late for that
        // early read. UseSetting seeds WebHostBuilder's own settings, which the minimal
        // hosting model folds into IConfiguration immediately — early enough for
        // AddInfrastructure's read to see it, proven by this test failing without it.
        using var factory = TestFactory.Create(fixture).WithWebHostBuilder(b =>
        {
            b.UseSetting("MarketData:Source", "Yahoo");

            // Long enough that no poll fires inside the test process — this test only
            // asserts which hosted service got registered.
            b.UseSetting("MarketData:PollInterval", "01:00:00");
        });

        var hosted = factory.Services.GetServices<IHostedService>();

        Assert.Contains(hosted, s => s is YahooPriceFeedService);
        Assert.DoesNotContain(hosted, s => s is FakeTickService);
    }

    [Fact]
    public void An_unrecognised_source_fails_startup_validation()
    {
        using var factory = TestFactory.Create(fixture, new Dictionary<string, string?>
        {
            ["MarketData:Source"] = "Chaos"
        });

        var ex = Assert.Throws<OptionsValidationException>(() => factory.Services);

        Assert.Contains(nameof(MarketDataOptions.Source), ex.Message);
    }
}
