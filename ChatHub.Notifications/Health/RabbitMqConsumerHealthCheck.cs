using ChatHub.Notifications.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChatHub.Notifications.Health;

public sealed class RabbitMqConsumerHealthCheck(EventConsumer consumer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(consumer.IsConnected
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus,
                $"Not consuming {NotificationTopology.Queue}: the RabbitMQ connection is down or recovering."));
}
