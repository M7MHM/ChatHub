using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Messaging;

internal sealed class OutboxBacklogHealthCheck(
    ApplicationDbContext db,
    IOptions<MessagingSettings> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var pending = db.OutboxMessages.AsNoTracking().Where(m => m.ProcessedAtUtc == null);
        var oldest = await pending.MinAsync(m => (DateTime?)m.OccurredAtUtc, cancellationToken);
        if (oldest is null) return HealthCheckResult.Healthy();

        var age = timeProvider.GetUtcNow().UtcDateTime - oldest.Value;
        if (age <= options.Value.BacklogAlert) return HealthCheckResult.Healthy();

        var count = await pending.CountAsync(cancellationToken);
        return new HealthCheckResult(context.Registration.FailureStatus,
            $"{count} integration events are waiting in the outbox; the oldest is {age.TotalMinutes:0} minutes old " +
            $"(alert after {options.Value.BacklogAlertMinutes}).");
    }
}
