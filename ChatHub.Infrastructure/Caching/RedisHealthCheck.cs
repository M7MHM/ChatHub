using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace ChatHub.Infrastructure.Caching;

internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(context.Registration.FailureStatus,
                "Redis is unreachable; the API serves from the database and real-time delivery across instances is down.",
                exception);
        }
    }
}
