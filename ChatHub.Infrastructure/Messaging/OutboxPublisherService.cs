using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Messaging;

public sealed class OutboxPublisherService(
    IServiceScopeFactory scopeFactory,
    RabbitMqPublisher publisher,
    IOptions<MessagingSettings> options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private const int CleanupBatchSize = 5000;
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private MessagingSettings Settings => options.Value;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failuresInARow = 0;
        var nextCleanupAtUtc = DateTime.MinValue;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (UtcNow >= nextCleanupAtUtc)
                {
                    nextCleanupAtUtc = UtcNow + Settings.CleanupInterval;
                    try
                    {
                        await CleanupAsync(stoppingToken);
                    }
                    catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(exception, "Outbox cleanup failed; it runs again in {Interval}.", Settings.CleanupInterval);
                    }
                }

                TimeSpan delay;
                try
                {
                    var claimed = await PublishPendingAsync(stoppingToken);
                    failuresInARow = 0;
                    delay = claimed >= Settings.BatchSize ? TimeSpan.Zero : Settings.PollInterval;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    delay = BackoffDelay(failuresInARow++, Min(Settings.MaxRetryDelay, MaxReconnectDelay));
                    logger.LogWarning(exception,
                        "Publishing the outbox failed {Failures} time(s) in a row; integration events stay in the outbox, retrying in {Delay}.",
                        failuresInARow, delay);
                }

                if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        await publisher.ConnectAsync(cancellationToken);

        var batch = await ClaimBatchAsync(cancellationToken);
        if (batch.Count == 0) return 0;

        var failures = await publisher.PublishAsync(batch, cancellationToken);
        await RecordOutcomeAsync(batch, failures, cancellationToken);
        return batch.Count;
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var cutoff = UtcNow - Settings.Retention;
        var total = 0;
        int deleted;
        do
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            deleted = await db.Database.ExecuteSqlAsync($"""
                DELETE FROM "OutboxMessages"
                WHERE "Id" IN (
                    SELECT "Id" FROM "OutboxMessages"
                    WHERE "ProcessedAtUtc" < {cutoff}
                    LIMIT {CleanupBatchSize}
                    FOR UPDATE SKIP LOCKED)
                """, cancellationToken);
            total += deleted;
        }
        while (deleted == CleanupBatchSize);

        if (total > 0)
            logger.LogInformation("Outbox cleanup deleted {Count} integration events published before {Cutoff:O}.", total, cutoff);
        return total;
    }

    private async Task<List<OutboxEnvelope>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var now = UtcNow;
        var leasedUntil = now + Settings.Lease;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var batch = await db.Database.SqlQuery<OutboxEnvelope>($"""
            UPDATE "OutboxMessages"
            SET "LockedUntilUtc" = {leasedUntil}
            WHERE "Id" IN (
                SELECT "Id" FROM "OutboxMessages"
                WHERE "ProcessedAtUtc" IS NULL AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {now})
                ORDER BY "OccurredAtUtc", "Id"
                LIMIT {Settings.BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING "Id", "Type", "RoutingKey", "Payload", "OccurredAtUtc", "Attempts"
            """).ToListAsync(cancellationToken);

        return [.. batch.OrderBy(m => m.OccurredAtUtc).ThenBy(m => m.Id)];
    }

    private async Task RecordOutcomeAsync(IReadOnlyList<OutboxEnvelope> batch, IReadOnlyList<PublishFailure> failures,
        CancellationToken cancellationToken)
    {
        var now = UtcNow;
        var failedIds = failures.Select(f => f.Id).ToHashSet();
        var publishedIds = batch.Where(m => !failedIds.Contains(m.Id)).Select(m => m.Id).ToArray();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        if (publishedIds.Length > 0)
            await db.OutboxMessages
                .Where(m => publishedIds.Contains(m.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.ProcessedAtUtc, now)
                    .SetProperty(m => m.LockedUntilUtc, (DateTime?)null), cancellationToken);

        if (failures.Count == 0) return;

        var maxDelaySeconds = Settings.MaxRetryDelay.TotalSeconds;
        foreach (var group in failures.GroupBy(f => Truncate(f.Error)))
        {
            var ids = group.Select(f => f.Id).ToArray();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE "OutboxMessages"
                SET "Attempts" = "Attempts" + 1,
                    "LastError" = {group.Key},
                    "LockedUntilUtc" = {now} + LEAST({maxDelaySeconds}, power(2, "Attempts")) * interval '1 second'
                WHERE "Id" = ANY({ids})
                """, cancellationToken);
        }

        logger.LogWarning("{Failed} of {Count} integration events could not be published and are retried with backoff: {Error}",
            failures.Count, batch.Count, failures[0].Error);
    }

    private static TimeSpan BackoffDelay(int failures, TimeSpan max) =>
        Min(TimeSpan.FromSeconds(Math.Pow(2, Math.Min(failures, 16))), max);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Truncate(string error) =>
        error.Length <= OutboxMessage.MaxErrorLength ? error : error[..OutboxMessage.MaxErrorLength];
}
