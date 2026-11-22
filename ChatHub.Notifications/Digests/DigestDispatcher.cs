using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Digests;

public sealed class DigestDispatcher(
    IServiceScopeFactory scopeFactory,
    IEnumerable<INotificationChannel> channels,
    IOptions<NotificationSettings> options,
    TimeProvider timeProvider,
    ILogger<DigestDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxLoopDelay = TimeSpan.FromSeconds(30);

    private NotificationSettings Settings => options.Value;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failuresInARow = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan delay;
                try
                {
                    var claimed = await DispatchDueAsync(stoppingToken);
                    failuresInARow = 0;
                    delay = claimed >= Settings.BatchSize ? TimeSpan.Zero : Settings.PollInterval;
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    delay = Backoff(TimeSpan.FromSeconds(1), failuresInARow++, MaxLoopDelay);
                    logger.LogWarning(exception, "Dispatching digests failed {Failures} time(s) in a row; retrying in {Delay}.",
                        failuresInARow, delay);
                }

                if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var claimedAtUtc = UtcNow;
        var leasedUntilUtc = claimedAtUtc + Settings.Lease;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var claimed = await db.PendingDigests.FromSql($"""
                UPDATE "PendingDigests" AS p SET "LockedUntilUtc" = {leasedUntilUtc}
                FROM (
                    SELECT "UserId", "ConversationId" FROM "PendingDigests"
                    WHERE "FailedAtUtc" IS NULL AND "DueAtUtc" <= {claimedAtUtc}
                      AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {claimedAtUtc})
                    ORDER BY "DueAtUtc"
                    LIMIT {Settings.BatchSize}
                    FOR UPDATE SKIP LOCKED) AS due
                WHERE p."UserId" = due."UserId" AND p."ConversationId" = due."ConversationId"
                RETURNING p.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (claimed.Count == 0) return 0;

        claimed = [.. claimed.OrderBy(d => d.DueAtUtc)];
        var userIds = claimed.Select(d => d.UserId).Distinct().ToArray();
        var recipients = await db.Recipients.AsNoTracking()
            .Where(r => userIds.Contains(r.UserId))
            .ToDictionaryAsync(r => r.UserId, cancellationToken);
        var unread = await UnreadCountsAsync(db, claimed, cancellationToken);

        for (var i = 0; i < claimed.Count; i++)
        {
            if (UtcNow - claimedAtUtc > Settings.Lease / 2)
            {
                await ReleaseAsync(db, claimed.Skip(i).ToList(), leasedUntilUtc);
                break;
            }

            await DispatchAsync(db, claimed[i], recipients.GetValueOrDefault(claimed[i].UserId),
                unread.GetValueOrDefault((claimed[i].UserId, claimed[i].ConversationId)), cancellationToken);
        }

        return claimed.Count;
    }

    private async Task DispatchAsync(NotificationsDbContext db, PendingDigest digest, Recipient? recipient, int unreadCount,
        CancellationToken cancellationToken)
    {
        var deliverable = recipient is null ? [] : channels.Where(c => c.CanDeliverTo(recipient)).ToList();
        if (deliverable.Count == 0)
        {
            logger.LogInformation(
                "Dropping the digest for user {UserId} in conversation {ConversationId}: the recipient is {State}.",
                digest.UserId, digest.ConversationId, recipient is null ? "unknown" : recipient.IsDeleted ? "deleted" : "unreachable");
            await db.DeleteDigestAsync(digest.UserId, digest.ConversationId, CancellationToken.None);
            return;
        }

        if (unreadCount == 0)
        {
            await db.RemoveMessagesUpToAsync(digest.UserId, digest.ConversationId, digest.LastMessageAtUtc, Settings,
                resetDelivery: true, CancellationToken.None);
            return;
        }

        var notification = new DigestNotification(recipient!, digest.ConversationId, digest.ConversationType,
            digest.ConversationTitle, unreadCount, digest.LastSenderName, Settings.IncludePreview ? digest.LastPreview : null,
            digest.FirstUnreadAtUtc, digest.LastMessageAtUtc, Settings.ConversationLink(digest.ConversationId));

        try
        {
            foreach (var channel in deliverable)
                await channel.SendAsync(notification, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await FailAsync(db, digest, exception);
            return;
        }

        await CompleteAsync(db, digest, notification, deliverable);
    }

    private async Task CompleteAsync(NotificationsDbContext db, PendingDigest digest, DigestNotification notification,
        IReadOnlyList<INotificationChannel> deliveredBy)
    {
        var nowUtc = UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "DigestCooldowns" ("UserId", "ConversationId", "LastSentAtUtc")
            VALUES ({digest.UserId}, {digest.ConversationId}, {nowUtc})
            ON CONFLICT ("UserId", "ConversationId") DO UPDATE SET "LastSentAtUtc" = EXCLUDED."LastSentAtUtc"
            """, CancellationToken.None);
        await db.RemoveMessagesUpToAsync(digest.UserId, digest.ConversationId, digest.LastMessageAtUtc, Settings,
            resetDelivery: true, CancellationToken.None);

        db.SentNotifications.AddRange(deliveredBy.Select(channel => new SentNotification
        {
            UserId = digest.UserId,
            ConversationId = digest.ConversationId,
            Channel = channel.Name,
            MessageCount = notification.MessageCount,
            SentAtUtc = nowUtc
        }));
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        await transaction.CommitAsync(CancellationToken.None);
        logger.LogInformation("Sent a digest of {Count} message(s) to user {UserId} for conversation {ConversationId} via {Channels}.",
            notification.MessageCount, digest.UserId, digest.ConversationId, string.Join(", ", deliveredBy.Select(c => c.Name)));
    }

    private async Task FailAsync(NotificationsDbContext db, PendingDigest digest, Exception exception)
    {
        var nowUtc = UtcNow;
        var attempts = digest.Attempts + 1;
        var gaveUp = attempts >= Settings.MaxAttempts;
        var retryAtUtc = nowUtc + Backoff(Settings.RetryBaseDelay, attempts - 1, MaxRetryDelay);
        var error = exception.Message.Length <= PendingDigest.MaxErrorLength
            ? exception.Message
            : exception.Message[..PendingDigest.MaxErrorLength];

        await db.PendingDigests
            .Where(d => d.UserId == digest.UserId && d.ConversationId == digest.ConversationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Attempts, attempts)
                .SetProperty(d => d.LastError, error)
                .SetProperty(d => d.LockedUntilUtc, retryAtUtc)
                .SetProperty(d => d.FailedAtUtc, gaveUp ? nowUtc : null), CancellationToken.None);

        if (gaveUp)
            logger.LogError(exception,
                "Giving up on the digest for user {UserId} in conversation {ConversationId} after {Attempts} attempts.",
                digest.UserId, digest.ConversationId, attempts);
        else
            logger.LogWarning(exception,
                "Sending the digest for user {UserId} in conversation {ConversationId} failed (attempt {Attempts} of {MaxAttempts}); retrying at {RetryAt:O}.",
                digest.UserId, digest.ConversationId, attempts, Settings.MaxAttempts, retryAtUtc);
    }

    private static async Task<Dictionary<(Guid UserId, Guid ConversationId), int>> UnreadCountsAsync(NotificationsDbContext db,
        IReadOnlyList<PendingDigest> digests, CancellationToken cancellationToken)
    {
        var userIds = digests.Select(d => d.UserId).ToArray();
        var conversationIds = digests.Select(d => d.ConversationId).ToArray();
        var lastMessages = digests.Select(d => d.LastMessageAtUtc).ToArray();

        var counts = await db.Database.SqlQuery<UnreadCount>($"""
                SELECT m."UserId", m."ConversationId", count(*)::int AS "Count"
                FROM unnest({userIds}, {conversationIds}, {lastMessages}) AS k("UserId", "ConversationId", "LastMessageAtUtc")
                JOIN "DigestMessages" m ON m."UserId" = k."UserId" AND m."ConversationId" = k."ConversationId"
                                       AND m."SentAtUtc" <= k."LastMessageAtUtc"
                LEFT JOIN "ConversationReads" r ON r."UserId" = k."UserId" AND r."ConversationId" = k."ConversationId"
                WHERE r."ReadUpToUtc" IS NULL OR m."SentAtUtc" > r."ReadUpToUtc"
                GROUP BY m."UserId", m."ConversationId"
                """)
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(c => (c.UserId, c.ConversationId), c => c.Count);
    }

    private static Task ReleaseAsync(NotificationsDbContext db, IReadOnlyList<PendingDigest> digests, DateTime leasedUntilUtc)
    {
        var userIds = digests.Select(d => d.UserId).ToArray();
        var conversationIds = digests.Select(d => d.ConversationId).ToArray();
        return db.Database.ExecuteSqlAsync($"""
            UPDATE "PendingDigests" AS p SET "LockedUntilUtc" = NULL
            FROM unnest({userIds}, {conversationIds}) AS k("UserId", "ConversationId")
            WHERE p."UserId" = k."UserId" AND p."ConversationId" = k."ConversationId" AND p."LockedUntilUtc" = {leasedUntilUtc}
            """, CancellationToken.None);
    }

    private static TimeSpan Backoff(TimeSpan baseDelay, int exponent, TimeSpan max)
    {
        var delay = baseDelay * Math.Pow(2, Math.Min(exponent, 20));
        return delay < max ? delay : max;
    }

    private sealed record UnreadCount(Guid UserId, Guid ConversationId, int Count);
}
