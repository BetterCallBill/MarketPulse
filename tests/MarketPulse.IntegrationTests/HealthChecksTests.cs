using System.Net;

namespace MarketPulse.IntegrationTests;

[Collection(nameof(SqlServerCollection))]
public class HealthChecksTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Liveness_is_unchanged_and_dependency_free()
    {
        using var factory = TestFactory.Create(fixture);
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_reports_on_real_dependencies()
    {
        using var factory = TestFactory.Create(fixture);
        var response = await factory.CreateClient().GetAsync("/health/ready");

        // SQL (Testcontainers) is up. RabbitMQ may or may not be reachable in this
        // fixture — assert the endpoint exists and returns a health-check status code,
        // and that the body names both checks.
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("sqlserver", body);
        Assert.Contains("rabbitmq", body);
    }
}
