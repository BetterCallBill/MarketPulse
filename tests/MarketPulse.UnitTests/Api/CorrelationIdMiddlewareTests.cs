using System.Diagnostics;
using MarketPulse.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace MarketPulse.UnitTests.Api;

public class CorrelationIdMiddlewareTests
{
    [Fact]
    public async Task Tags_the_current_activity_and_opens_a_log_scope()
    {
        using var activity = new Activity("test").Start();
        var logger = new FakeLogger<CorrelationIdMiddleware>();
        string? scopedCorrelation = null;

        var middleware = new CorrelationIdMiddleware(context =>
        {
            // Prove the scope is open while downstream runs: log and inspect the scope.
            logger.LogInformation("inside");
            scopedCorrelation = context.Items[CorrelationIdMiddleware.HeaderName] as string;
            return Task.CompletedTask;
        }, logger);

        var http = new DefaultHttpContext();
        http.Request.Headers[CorrelationIdMiddleware.HeaderName] = "corr-123";

        await middleware.InvokeAsync(http);

        Assert.Equal("corr-123", scopedCorrelation);
        Assert.Equal("corr-123", activity.GetTagItem("correlation.id"));
        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Contains(record.Scopes, s =>
            s is IReadOnlyList<KeyValuePair<string, object?>> kvs
            && kvs.Any(kv => kv.Key == "CorrelationId" && (string?)kv.Value == "corr-123"));
    }
}
