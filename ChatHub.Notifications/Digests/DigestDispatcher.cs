using System.Collections.Concurrent;
using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Sending;
using ChatHub.Notifications.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Digests;

public sealed class DigestDispatcher(
    IServiceScopeFactory scopeFactory,
    IEnumerable<INotificationChannel> channels,
    SendQuota quota,
    EmailSuppression suppression,
    IOptions<NotificationSettings> options,
    TimeProvider timeProvider,
    ILogger<DigestDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxLoopDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLimitedDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecipientWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTime> _limitLoggedUntil = new();
    private SendBudget? _lastBudget;

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
                    delay = claimed >= Settings.BatchSize ? TimeSpan.Zero : IdleDelay();
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    delay = Backoff(TimeSpan.FromSeconds(1), failuresInARow++, MaxLoopDelay);
                    logger.LogWarning(exception, "Dispatching notifications failed {Failures} time(s) in a row; retrying in {Delay}.",
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        var budget = quota.Budget(await quota.GetUsageAsync(db, UtcNow, cancellationToken));
        _lastBudget = budget;

        var emails = await SendOutgoingEmailsAsync(db, budget, cancellationToken);
        var digests = await DispatchDigestsAsync(db, budget, cancellationToken);
        return emails + digests;
    }

    private TimeSpan IdleDelay()
    {
        if (_lastBudget is not { Transactional: 0 } budget) return Settings.PollInterval;
        var wait = budget.NextCapacityAtUtc(NotificationKind.Transactional) - UtcNow;
        return wait < Settings.PollInterval ? Settings.PollInterval : wait > MaxLimitedDelay ? MaxLimitedDelay : wait;
    }

    private async Task<int> SendOutgoingEmailsAsync(NotificationsDbContext db, SendBudget budget, CancellationToken cancellationToken)
    {
        var claimedAtUtc = UtcNow;
        var limit = Math.Min(Settings.BatchSize, budget.Transactional);
        if (limit == 0)
        {
            if (await db.OutgoingEmails.AnyAsync(e => e.FailedAtUtc == null && (e.LockedUntilUtc == null || e.LockedUntilUtc <= claimedAtUtc),
                    cancellationToken))
                LogLimited(budget, NotificationKind.Transactional, "verification emails");
            return 0;
        }

        var leasedUntilUtc = claimedAtUtc + Settings.Lease;
        var claimed = await db.OutgoingEmails.FromSql($"""
                UPDATE "OutgoingEmails" AS e SET "LockedUntilUtc" = {leasedUntilUtc}
                FROM (
                    SELECT "Id" FROM "OutgoingEmails"
                    WHERE "FailedAtUtc" IS NULL AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {claimedAtUtc})
                    ORDER BY "CreatedAtUtc"
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED) AS due
                WHERE e."Id" = due."Id"
                RETURNING e.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        claimed = [.. claimed.OrderBy(e => e.CreatedAtUtc)];
        for (var i = 0; i < claimed.Count; i++)
        {
            if (UtcNow - claimedAtUtc > Settings.Lease / 2)
            {
                var rest = claimed.Skip(i).Select(e => e.Id).ToArray();
                await db.OutgoingEmails.Where(e => rest.Contains(e.Id) && e.LockedUntilUtc == leasedUntilUtc)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.LockedUntilUtc, (DateTime?)null), CancellationToken.None);
                break;
            }

            await SendOutgoingAsync(db, claimed[i], leasedUntilUtc, budget, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task SendOutgoingAsync(NotificationsDbContext db, OutgoingEmail email, DateTime leasedUntilUtc, SendBudget budget,
        CancellationToken cancellationToken)
    {
        if (email.ExpiresAtUtc <= UtcNow)
        {
            await DeleteEmailAsync(db, email, leasedUntilUtc);
            logger.LogInformation("Dropping the {Template} email for user {UserId}: its link expired at {ExpiresAt:O}.",
                email.Template, email.UserId, email.ExpiresAtUtc);
            return;
        }

        if (suppression.IsSuppressed(email.ToAddress))
        {
            await DeleteEmailAsync(db, email, leasedUntilUtc);
            quota.CountSkipped();
            logger.LogInformation("Skipping the {Template} email for user {UserId}: the address is in a suppressed domain.",
                email.Template, email.UserId);
            return;
        }

        Notification notification = email.Template switch
        {
            OutgoingEmailTemplate.EmailVerification => new EmailVerificationNotification(email.UserId, email.ToAddress,
                email.DisplayName, email.Link, email.ExpiresAtUtc),
            OutgoingEmailTemplate.PasswordReset => new PasswordResetNotification(email.UserId, email.ToAddress,
                email.DisplayName, email.Link, email.ExpiresAtUtc),
            OutgoingEmailTemplate.PasswordChanged => new PasswordChangedNotification(email.UserId, email.ToAddress,
                email.DisplayName, email.Link),
            _ => throw new NotSupportedException($"Unknown email template {email.Template}.")
        };
        var deliverable = channels.Where(c => c.CanDeliver(notification)).ToList();
        if (deliverable.Count == 0)
        {
            await DeleteEmailAsync(db, email, leasedUntilUtc);
            logger.LogWarning("No channel can send the {Template} email for user {UserId}.", email.Template, email.UserId);
            return;
        }

        try
        {
            foreach (var channel in deliverable)
                await channel.SendAsync(notification, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            await FailEmailAsync(db, email, exception);
            return;
        }

        budget.Spend(NotificationKind.Transactional);
        await CompleteEmailAsync(db, email, deliverable, leasedUntilUtc);
    }

    private async Task CompleteEmailAsync(NotificationsDbContext db, OutgoingEmail email, IReadOnlyList<INotificationChannel> deliveredBy,
        DateTime leasedUntilUtc)
    {
        var nowUtc = UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);

        var removed = await db.OutgoingEmails
            .Where(e => e.Id == email.Id && e.LockedUntilUtc == leasedUntilUtc)
            .ExecuteDeleteAsync(CancellationToken.None);
        if (removed == 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            logger.LogWarning("The {Template} email {EmailId} was completed by another dispatcher after its lease expired; not recorded twice.",
                email.Template, email.Id);
            return;
        }

        db.SentNotifications.AddRange(deliveredBy.Select(channel => new SentNotification
        {
            UserId = email.UserId,
            Channel = channel.Name,
            Kind = NotificationKind.Transactional,
            SentAtUtc = nowUtc
        }));
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        await transaction.CommitAsync(CancellationToken.None);

        logger.LogInformation("Sent the {Template} email to user {UserId} via {Channels}.",
            email.Template, email.UserId, string.Join(", ", deliveredBy.Select(c => c.Name)));
    }

    private static Task DeleteEmailAsync(NotificationsDbContext db, OutgoingEmail email, DateTime leasedUntilUtc) =>
        db.OutgoingEmails.Where(e => e.Id == email.Id && e.LockedUntilUtc == leasedUntilUtc)
            .ExecuteDeleteAsync(CancellationToken.None);

    private async Task FailEmailAsync(NotificationsDbContext db, OutgoingEmail email, Exception exception)
    {
        var nowUtc = UtcNow;
        var attempts = email.Attempts + 1;
        var gaveUp = attempts >= Settings.MaxAttempts;
        var retryAtUtc = nowUtc + Backoff(Settings.RetryBaseDelay, attempts - 1, MaxRetryDelay);
        var error = Truncate(exception.Message, OutgoingEmail.MaxErrorLength);

        await db.OutgoingEmails
            .Where(e => e.Id == email.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Attempts, attempts)
                .SetProperty(e => e.LastError, error)
                .SetProperty(e => e.LockedUntilUtc, retryAtUtc)
                .SetProperty(e => e.FailedAtUtc, gaveUp ? nowUtc : null), CancellationToken.None);

        if (gaveUp)
            logger.LogError(exception, "Giving up on the {Template} email for user {UserId} after {Attempts} attempts.",
                email.Template, email.UserId, attempts);
        else
            logger.LogWarning(exception,
                "Sending the {Template} email for user {UserId} failed (attempt {Attempts} of {MaxAttempts}); retrying at {RetryAt:O}.",
                email.Template, email.UserId, attempts, Settings.MaxAttempts, retryAtUtc);
    }

    private async Task<int> DispatchDigestsAsync(NotificationsDbContext db, SendBudget budget, CancellationToken cancellationToken)
    {
        var claimedAtUtc = UtcNow;
        var limit = Math.Min(Settings.BatchSize, budget.Digests);
        if (limit == 0)
        {
            if (await db.PendingDigests.AnyAsync(d => d.FailedAtUtc == null && d.DueAtUtc <= claimedAtUtc &&
                                                      (d.LockedUntilUtc == null || d.LockedUntilUtc <= claimedAtUtc), cancellationToken))
                LogLimited(budget, NotificationKind.Digest, "digests");
            return 0;
        }

        var leasedUntilUtc = claimedAtUtc + Settings.Lease;
        var claimed = await db.PendingDigests.FromSql($"""
                UPDATE "PendingDigests" AS p SET "LockedUntilUtc" = {leasedUntilUtc}
                FROM (
                    SELECT "UserId", "ConversationId" FROM "PendingDigests"
                    WHERE "FailedAtUtc" IS NULL AND "DueAtUtc" <= {claimedAtUtc}
                      AND ("LockedUntilUtc" IS NULL OR "LockedUntilUtc" <= {claimedAtUtc})
                    ORDER BY "DueAtUtc"
                    LIMIT {limit}
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
        var online = (await db.RecipientPresence.AsNoTracking()
                .Where(p => userIds.Contains(p.UserId) && p.IsOnline)
                .Select(p => p.UserId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var hourAgo = claimedAtUtc - RecipientWindow;
        var recent = await db.SentNotifications.AsNoTracking()
            .Where(s => s.Kind == NotificationKind.Digest && s.SentAtUtc > hourAgo && userIds.Contains(s.UserId))
            .GroupBy(s => s.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count(), Oldest = g.Min(s => s.SentAtUtc) })
            .ToDictionaryAsync(g => g.UserId, g => new RecentDigests(g.Count, g.Oldest), cancellationToken);
        var unread = await UnreadCountsAsync(db, claimed, claimedAtUtc - Settings.MaxMessageAge, cancellationToken);

        var batch = new DigestBatch(leasedUntilUtc, online, recent, budget);
        for (var i = 0; i < claimed.Count; i++)
        {
            if (UtcNow - claimedAtUtc > Settings.Lease / 2)
            {
                await ReleaseAsync(db, claimed.Skip(i).ToList(), leasedUntilUtc);
                break;
            }

            await DispatchAsync(db, claimed[i], recipients.GetValueOrDefault(claimed[i].UserId),
                unread.GetValueOrDefault((claimed[i].UserId, claimed[i].ConversationId)), batch, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task DispatchAsync(NotificationsDbContext db, PendingDigest digest, Recipient? recipient, int unreadCount,
        DigestBatch batch, CancellationToken cancellationToken)
    {
        if (recipient is not null && suppression.IsSuppressed(recipient.Email))
        {
            quota.CountSkipped();
            logger.LogDebug("Skipping the digest for user {UserId} in conversation {ConversationId}: the address is in a suppressed domain.",
                digest.UserId, digest.ConversationId);
            await db.DeleteDigestAsync(digest.UserId, digest.ConversationId, CancellationToken.None);
            return;
        }

        var deliverable = recipient is null ? [] : channels.Where(c => c.CanDeliverTo(recipient)).ToList();
        var awaitsVerification = recipient is { AwaitsEmailVerification: true };
        if (deliverable.Count == 0 && !awaitsVerification)
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

        if (deliverable.Count == 0)
        {
            await HoldAsync(db, digest, batch.LeasedUntilUtc, UtcNow + Settings.HoldRecheck, "the email address is verified");
            return;
        }

        if (batch.Online.Contains(digest.UserId))
        {
            await HoldAsync(db, digest, batch.LeasedUntilUtc, UtcNow + Settings.HoldRecheck, "the recipient goes offline");
            return;
        }

        var recent = batch.Recent.GetValueOrDefault(digest.UserId) ?? new RecentDigests(0, null);
        if (recent.Count >= Settings.MaxEmailsPerRecipientPerHour)
        {
            await HoldAsync(db, digest, batch.LeasedUntilUtc, (recent.OldestSentAtUtc ?? UtcNow) + RecipientWindow,
                "the recipient's hourly limit frees up");
            return;
        }

        if (batch.Budget.Digests == 0)
        {
            await ReleaseAsync(db, [digest], batch.LeasedUntilUtc);
            LogLimited(batch.Budget, NotificationKind.Digest, "digests");
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

        batch.Budget.Spend(NotificationKind.Digest);
        batch.Recent[digest.UserId] = new RecentDigests(recent.Count + 1, recent.OldestSentAtUtc ?? UtcNow);
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
            Kind = NotificationKind.Digest,
            MessageCount = notification.MessageCount,
            SentAtUtc = nowUtc
        }));
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        await transaction.CommitAsync(CancellationToken.None);
        logger.LogInformation("Sent a digest of {Count} message(s) to user {UserId} for conversation {ConversationId} via {Channels}.",
            notification.MessageCount, digest.UserId, digest.ConversationId, string.Join(", ", deliveredBy.Select(c => c.Name)));
    }

    private async Task HoldAsync(NotificationsDbContext db, PendingDigest digest, DateTime leasedUntilUtc, DateTime untilUtc, string until)
    {
        await db.PendingDigests
            .Where(d => d.UserId == digest.UserId && d.ConversationId == digest.ConversationId && d.LockedUntilUtc == leasedUntilUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LockedUntilUtc, untilUtc), CancellationToken.None);

        logger.LogDebug("Holding the digest for user {UserId} in conversation {ConversationId} until {Until}; next check at {RecheckAt:O}.",
            digest.UserId, digest.ConversationId, until, untilUtc);
    }

    private async Task FailAsync(NotificationsDbContext db, PendingDigest digest, Exception exception)
    {
        var nowUtc = UtcNow;
        var attempts = digest.Attempts + 1;
        var gaveUp = attempts >= Settings.MaxAttempts;
        var retryAtUtc = nowUtc + Backoff(Settings.RetryBaseDelay, attempts - 1, MaxRetryDelay);
        var error = Truncate(exception.Message, PendingDigest.MaxErrorLength);

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

    private void LogLimited(SendBudget budget, NotificationKind kind, string what)
    {
        var limit = budget.ExhaustedLimit(kind);
        if (limit is null) return;

        var nowUtc = UtcNow;
        var untilUtc = budget.NextCapacityAtUtc(kind);
        if (_limitLoggedUntil.TryGetValue(limit, out var loggedUntil) && nowUtc < loggedUntil) return;
        _limitLoggedUntil[limit] = untilUtc > nowUtc ? untilUtc : nowUtc + TimeSpan.FromMinutes(1);

        logger.LogWarning("Sending limit reached ({Limit}): {What} stay pending until about {Until:O}.", limit, what, untilUtc);
    }

    private static async Task<Dictionary<(Guid UserId, Guid ConversationId), int>> UnreadCountsAsync(NotificationsDbContext db,
        IReadOnlyList<PendingDigest> digests, DateTime notBeforeUtc, CancellationToken cancellationToken)
    {
        var userIds = digests.Select(d => d.UserId).ToArray();
        var conversationIds = digests.Select(d => d.ConversationId).ToArray();
        var lastMessages = digests.Select(d => d.LastMessageAtUtc).ToArray();

        var counts = await db.Database.SqlQuery<UnreadCount>($"""
                SELECT m."UserId", m."ConversationId", count(*)::int AS "Count"
                FROM unnest({userIds}, {conversationIds}, {lastMessages}) AS k("UserId", "ConversationId", "LastMessageAtUtc")
                JOIN "DigestMessages" m ON m."UserId" = k."UserId" AND m."ConversationId" = k."ConversationId"
                                       AND m."SentAtUtc" <= k."LastMessageAtUtc" AND m."SentAtUtc" >= {notBeforeUtc}
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

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private sealed record UnreadCount(Guid UserId, Guid ConversationId, int Count);

    private sealed record RecentDigests(int Count, DateTime? OldestSentAtUtc);

    private sealed record DigestBatch(
        DateTime LeasedUntilUtc,
        HashSet<Guid> Online,
        Dictionary<Guid, RecentDigests> Recent,
        SendBudget Budget);
}
