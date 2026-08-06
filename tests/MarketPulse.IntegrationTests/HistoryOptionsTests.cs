namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class HistoryOptionsTests(SqlServerFixture fixture)
{
    [Fact]
    public void Zero_retention_days_fails_startup_validation()
    {
        using var factory = TestFactory.Create(fixture, new Dictionary<string, string?>
        {
            ["History:RetentionDays"] = "0"
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("HistoryOptions", ex.ToString());
    }

    [Fact]
    public void Defaults_are_valid_without_any_History_section()
    {
        using var factory = TestFactory.Create(fixture);
        using var client = factory.CreateClient(); // boots; would throw if defaults invalid
    }
}
