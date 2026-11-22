using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Digests;

public sealed class MessageSentHandler(
    NotificationsDbContext db,
    IEnumerable<INotificationChannel> channels,
    IOptions<NotificationSettings> options)
{
    public async Task HandleAsync(MessageSent message, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (nowUtc - message.SentAtUtc > settings.MaxMessageAge) return;

        var muted = message.MutedRecipientUserIds.ToHashSet();
        var candidates = message.RecipientUserIds
            .Where(id => id != message.SenderId && !muted.Contains(id))
            .Distinct()
            .ToArray();
        if (candidates.Length == 0) return;

        var known = await db.Recipients.AsNoTracking().Where(r => candidates.Contains(r.UserId)).ToListAsync(cancellationToken);
        var unreachable = known.Where(r => !channels.Any(c => c.CanDeliverTo(r))).Select(r => r.UserId).ToHashSet();
        var recipients = candidates.Where(id => !unreachable.Contains(id)).ToArray();
        if (recipients.Length == 0) return;

        var conversationType = message.ConversationType.ToString();
        var title = Truncate(message.ConversationTitle, PendingDigest.MaxTitleLength);
        var sender = Truncate(message.SenderDisplayName, PendingDigest.MaxSenderNameLength)!;
        var preview = Truncate(message.Preview, PendingDigest.MaxPreviewLength);

        await db.Database.ExecuteSqlAsync($"""
            WITH unread AS (
                SELECT u."UserId" FROM unnest({recipients}) AS u("UserId")
                WHERE NOT EXISTS (
                    SELECT 1 FROM "ConversationReads" r
                    WHERE r."UserId" = u."UserId" AND r."ConversationId" = {message.ConversationId}
                      AND r."ReadUpToUtc" >= {message.SentAtUtc})),
            folded AS (
                INSERT INTO "DigestMessages" ("UserId", "ConversationId", "ChatMessageId", "SentAtUtc")
                SELECT "UserId", {message.ConversationId}, {message.ChatMessageId}, {message.SentAtUtc} FROM unread
                ON CONFLICT DO NOTHING
                RETURNING "UserId")
            INSERT INTO "PendingDigests" AS p ("UserId", "ConversationId", "ConversationType", "ConversationTitle",
                "FirstUnreadAtUtc", "LastMessageAtUtc", "MessageCount", "LastSenderName", "LastPreview", "DueAtUtc", "Attempts")
            SELECT f."UserId", {message.ConversationId}, {conversationType}, CAST({title} AS text),
                   {message.SentAtUtc}, {message.SentAtUtc}, 1, {sender}, CAST({preview} AS text),
                   GREATEST({message.SentAtUtc} + {settings.DigestDelay},
                            COALESCE(cd."LastSentAtUtc" + {settings.Cooldown}, {message.SentAtUtc} + {settings.DigestDelay})),
                   0
            FROM folded f
            LEFT JOIN "DigestCooldowns" cd ON cd."UserId" = f."UserId" AND cd."ConversationId" = {message.ConversationId}
            ON CONFLICT ("UserId", "ConversationId") DO UPDATE SET
                "MessageCount" = p."MessageCount" + 1,
                "FirstUnreadAtUtc" = LEAST(p."FirstUnreadAtUtc", EXCLUDED."FirstUnreadAtUtc"),
                "LastMessageAtUtc" = GREATEST(p."LastMessageAtUtc", EXCLUDED."LastMessageAtUtc"),
                "LastSenderName" = CASE WHEN EXCLUDED."LastMessageAtUtc" >= p."LastMessageAtUtc"
                                        THEN EXCLUDED."LastSenderName" ELSE p."LastSenderName" END,
                "LastPreview" = CASE WHEN EXCLUDED."LastMessageAtUtc" >= p."LastMessageAtUtc"
                                     THEN EXCLUDED."LastPreview" ELSE p."LastPreview" END,
                "ConversationTitle" = CASE WHEN EXCLUDED."LastMessageAtUtc" >= p."LastMessageAtUtc"
                                           THEN EXCLUDED."ConversationTitle" ELSE p."ConversationTitle" END,
                "DueAtUtc" = LEAST(p."DueAtUtc", EXCLUDED."DueAtUtc"),
                "Attempts" = CASE WHEN p."FailedAtUtc" IS NULL THEN p."Attempts" ELSE 0 END,
                "FailedAtUtc" = NULL
            """, cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
