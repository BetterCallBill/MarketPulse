using Microsoft.EntityFrameworkCore;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class TickPersistenceIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Fake_mode_ticks_reach_the_database()
    {
        using var factory = TestFactory.Create(fixture, new Dictionary<string, string?>
        {
            ["History:FlushIntervalSeconds"] = "1"
        });
        using var client = factory.CreateClient(); // boots the host and its hosted services

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = fixture.CreateContext();
            if (await db.PriceTicks.AnyAsync())
            {
                return; // FakeTickService → channel → broadcaster → sink → buffer → writer → table
            }

            await Task.Delay(250);
        }

        Assert.Fail("No ticks were persisted within 15 seconds of startup.");
    }
}
