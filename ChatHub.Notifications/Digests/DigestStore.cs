using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Digests;

public static class DigestStore
{
    public static async Task RemoveMessagesUpToAsync(this NotificationsDbContext db, Guid userId, Guid conversationId,
        DateTime upToUtc, NotificationSettings settings, bool resetDelivery, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "DigestMessages"
            WHERE "UserId" = {userId} AND "ConversationId" = {conversationId} AND "SentAtUtc" <= {upToUtc}
            """, cancellationToken);

        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "PendingDigests" p
            WHERE p."UserId" = {userId} AND p."ConversationId" = {conversationId}
              AND NOT EXISTS (SELECT 1 FROM "DigestMessages" m WHERE m."UserId" = {userId} AND m."ConversationId" = {conversationId})
            """, cancellationToken);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "PendingDigests" AS p SET
                "MessageCount" = remaining.n,
                "FirstUnreadAtUtc" = remaining.first,
                "DueAtUtc" = GREATEST(remaining.first + {settings.DigestDelay},
                                      COALESCE(cd."LastSentAtUtc" + {settings.Cooldown}, remaining.first + {settings.DigestDelay})),
                "LockedUntilUtc" = CASE WHEN {resetDelivery} THEN NULL ELSE p."LockedUntilUtc" END,
                "Attempts" = CASE WHEN {resetDelivery} THEN 0 ELSE p."Attempts" END,
                "LastError" = CASE WHEN {resetDelivery} THEN NULL ELSE p."LastError" END
            FROM (SELECT count(*)::int AS n, min("SentAtUtc") AS first FROM "DigestMessages"
                  WHERE "UserId" = {userId} AND "ConversationId" = {conversationId}) AS remaining
            LEFT JOIN "DigestCooldowns" cd ON cd."UserId" = {userId} AND cd."ConversationId" = {conversationId}
            WHERE p."UserId" = {userId} AND p."ConversationId" = {conversationId}
            """, cancellationToken);
    }

    public static async Task RecomputeDigestsAsync(this NotificationsDbContext db, Guid conversationId, Guid[] userIds,
        NotificationSettings settings, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM "PendingDigests" p
            WHERE p."ConversationId" = {conversationId} AND p."UserId" = ANY({userIds})
              AND NOT EXISTS (SELECT 1 FROM "DigestMessages" m WHERE m."UserId" = p."UserId" AND m."ConversationId" = {conversationId})
            """, cancellationToken);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "PendingDigests" AS p SET
                "MessageCount" = r.n,
                "FirstUnreadAtUtc" = r.first,
                "LastMessageAtUtc" = r.last,
                "LastSenderName" = COALESCE(l."SenderName", p."LastSenderName"),
                "LastPreview" = l."Preview",
                "DueAtUtc" = GREATEST(p."DueAtUtc", r.first + {settings.DigestDelay})
            FROM (SELECT m."UserId", count(*)::int AS n, min(m."SentAtUtc") AS first, max(m."SentAtUtc") AS last
                  FROM "DigestMessages" m
                  WHERE m."ConversationId" = {conversationId} AND m."UserId" = ANY({userIds})
                  GROUP BY m."UserId") AS r
            CROSS JOIN LATERAL (
                  SELECT s."SenderName", s."Preview"
                  FROM "DigestMessages" m2
                  LEFT JOIN "MessageSnapshots" s ON s."ChatMessageId" = m2."ChatMessageId"
                  WHERE m2."UserId" = r."UserId" AND m2."ConversationId" = {conversationId}
                  ORDER BY m2."SentAtUtc" DESC, m2."ChatMessageId" DESC
                  LIMIT 1) AS l
            WHERE p."UserId" = r."UserId" AND p."ConversationId" = {conversationId}
            """, cancellationToken);
    }

    public static async Task DeleteDigestAsync(this NotificationsDbContext db, Guid userId, Guid conversationId,
        CancellationToken cancellationToken)
    {
        await db.DigestMessages.Where(m => m.UserId == userId && m.ConversationId == conversationId).ExecuteDeleteAsync(cancellationToken);
        await db.PendingDigests.Where(d => d.UserId == userId && d.ConversationId == conversationId).ExecuteDeleteAsync(cancellationToken);
    }
}
