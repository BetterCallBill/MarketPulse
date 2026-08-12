using MarketPulse.Api.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using NSubstitute;

namespace MarketPulse.UnitTests.Api;

public class RabbitMqHealthCheckTests
{
    [Fact]
    public async Task Healthy_when_a_channel_opens()
    {
        var channel = Substitute.For<IChannel>();
        var check = new RabbitMqHealthCheck((_, _) => Task.FromResult(channel));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        await channel.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Unhealthy_when_the_broker_refuses()
    {
        var check = new RabbitMqHealthCheck(
            (_, _) => Task.FromException<IChannel>(new InvalidOperationException("down")));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
