using MarketPulse.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace MarketPulse.Api.Health;

/// <summary>Readiness = "can I open a channel right now". Same substitution seam as the
/// other messaging components: the single capability, not the sealed connection.</summary>
public sealed class RabbitMqHealthCheck(
    Func<bool, CancellationToken, Task<IChannel>> createChannel) : IHealthCheck
{
    public RabbitMqHealthCheck(RabbitMqConnection connection)
        : this(connection.CreateChannelAsync)
    {
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await createChannel(false, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot open a RabbitMQ channel.", ex);
        }
    }
}
