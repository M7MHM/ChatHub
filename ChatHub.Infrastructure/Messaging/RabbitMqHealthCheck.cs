using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChatHub.Infrastructure.Messaging;

internal sealed class RabbitMqHealthCheck(RabbitMqPublisher publisher) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(publisher.IsConnected
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus,
                "RabbitMQ is not connected; integration events wait in the outbox until it is back. " +
                (publisher.LastConnectionError ?? "The publisher has not connected yet.")));
}
