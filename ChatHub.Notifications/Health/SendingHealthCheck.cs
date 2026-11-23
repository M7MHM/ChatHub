using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Sending;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Health;

public sealed class SendingHealthCheck(
    IServiceScopeFactory scopeFactory,
    SendQuota quota,
    IOptions<NotificationSettings> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var usage = await quota.GetUsageAsync(db, nowUtc, cancellationToken);
        var waiting = await db.Database.SqlQuery<Waiting>($"""
                SELECT
                    (SELECT count(*)::int FROM "OutgoingEmails"
                     WHERE "FailedAtUtc" IS NULL AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {nowUtc})) AS "Transactional",
                    (SELECT min(GREATEST("CreatedAtUtc", COALESCE("LockedUntilUtc", "CreatedAtUtc"))) FROM "OutgoingEmails"
                     WHERE "FailedAtUtc" IS NULL AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {nowUtc})) AS "OldestTransactionalUtc",
                    (SELECT count(*)::int FROM "PendingDigests"
                     WHERE "FailedAtUtc" IS NULL AND "DueAtUtc" <= {nowUtc}
                       AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {nowUtc})) AS "Digests",
                    (SELECT min(GREATEST("DueAtUtc", COALESCE("LockedUntilUtc", "DueAtUtc"))) FROM "PendingDigests"
                     WHERE "FailedAtUtc" IS NULL AND "DueAtUtc" <= {nowUtc}
                       AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {nowUtc})) AS "OldestDigestUtc"
                """)
            .SingleAsync(cancellationToken);

        var oldest = new[] { waiting.OldestTransactionalUtc, waiting.OldestDigestUtc }.Where(t => t is not null).Min();
        var longestWait = oldest is null ? TimeSpan.Zero : nowUtc - oldest.Value;
        var data = new Dictionary<string, object>
        {
            ["sentLastMinute"] = usage.LastMinute,
            ["digestsLastMinute"] = usage.DigestsLastMinute,
            ["maxEmailsPerMinute"] = settings.MaxEmailsPerMinute,
            ["maxDigestsPerMinute"] = settings.MaxDigestsPerMinute,
            ["sentToday"] = usage.Today,
            ["transactionalToday"] = usage.TransactionalToday,
            ["digestsToday"] = usage.DigestsToday,
            ["maxEmailsPerDay"] = settings.MaxEmailsPerDay,
            ["maxDigestsPerDay"] = settings.MaxDigestsPerDay,
            ["transactionalWaiting"] = waiting.Transactional,
            ["digestsWaiting"] = waiting.Digests,
            ["longestWaitSeconds"] = (int)longestWait.TotalSeconds,
            ["skippedSuppressedSinceStart"] = quota.SkippedSuppressed
        };

        return longestWait > settings.SendBacklogAlert
            ? new HealthCheckResult(context.Registration.FailureStatus,
                $"Emails have waited {(int)longestWait.TotalMinutes} min to be sent (limit or SMTP problem).", data: data)
            : HealthCheckResult.Healthy($"{usage.Today} of {settings.MaxEmailsPerDay} emails sent today.", data);
    }

    private sealed record Waiting(int Transactional, DateTime? OldestTransactionalUtc, int Digests, DateTime? OldestDigestUtc);
}
