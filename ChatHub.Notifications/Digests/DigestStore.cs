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

    public static async Task DeleteDigestAsync(this NotificationsDbContext db, Guid userId, Guid conversationId,
        CancellationToken cancellationToken)
    {
        await db.DigestMessages.Where(m => m.UserId == userId && m.ConversationId == conversationId).ExecuteDeleteAsync(cancellationToken);
        await db.PendingDigests.Where(d => d.UserId == userId && d.ConversationId == conversationId).ExecuteDeleteAsync(cancellationToken);
    }
}
