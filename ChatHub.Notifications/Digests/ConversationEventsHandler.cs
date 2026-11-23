using ChatHub.Contracts.Events;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Digests;

public sealed class ConversationEventsHandler(
    NotificationsDbContext db,
    ConversationReadHandler reads,
    IOptions<NotificationSettings> options)
{
    public Task MemberRemovedAsync(Guid conversationId, Guid userId, DateTime removedAtUtc, CancellationToken cancellationToken) =>
        reads.MarkReadUpToAsync(userId, conversationId, removedAtUtc, cancellationToken);

    public Task MuteChangedAsync(ConversationMuteChanged change, CancellationToken cancellationToken) =>
        change.IsMuted
            ? reads.MarkReadUpToAsync(change.UserId, change.ConversationId, change.OccurredAtUtc, cancellationToken)
            : Task.CompletedTask;

    public async Task MessageEditedAsync(MessageEdited edit, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var preview = edit.Preview.Length <= PendingDigest.MaxPreviewLength ? edit.Preview : edit.Preview[..PendingDigest.MaxPreviewLength];

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "MessageSnapshots" AS s ("ChatMessageId", "ConversationId", "Preview", "EditedAtUtc", "RecordedAtUtc")
            VALUES ({edit.ChatMessageId}, {edit.ConversationId}, {preview}, {edit.EditedAtUtc}, {nowUtc})
            ON CONFLICT ("ChatMessageId") DO UPDATE SET
                "Preview" = CASE WHEN s."DeletedAtUtc" IS NULL AND (s."EditedAtUtc" IS NULL OR s."EditedAtUtc" < EXCLUDED."EditedAtUtc")
                                 THEN EXCLUDED."Preview" ELSE s."Preview" END,
                "EditedAtUtc" = GREATEST(s."EditedAtUtc", EXCLUDED."EditedAtUtc"),
                "RecordedAtUtc" = EXCLUDED."RecordedAtUtc"
            """, cancellationToken);

        await db.Database.ExecuteSqlAsync($"""
            UPDATE "PendingDigests" AS p SET "LastPreview" = s."Preview"
            FROM "DigestMessages" m
            JOIN "MessageSnapshots" s ON s."ChatMessageId" = m."ChatMessageId"
            WHERE m."ChatMessageId" = {edit.ChatMessageId} AND s."DeletedAtUtc" IS NULL
              AND p."UserId" = m."UserId" AND p."ConversationId" = m."ConversationId"
              AND NOT EXISTS (
                  SELECT 1 FROM "DigestMessages" n
                  WHERE n."UserId" = m."UserId" AND n."ConversationId" = m."ConversationId"
                    AND (n."SentAtUtc", n."ChatMessageId") > (m."SentAtUtc", m."ChatMessageId"))
            """, cancellationToken);
    }

    public async Task MessageDeletedAsync(MessageDeleted deletion, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "MessageSnapshots" AS s ("ChatMessageId", "ConversationId", "DeletedAtUtc", "RecordedAtUtc")
            VALUES ({deletion.ChatMessageId}, {deletion.ConversationId}, {deletion.OccurredAtUtc}, {nowUtc})
            ON CONFLICT ("ChatMessageId") DO UPDATE SET
                "DeletedAtUtc" = COALESCE(s."DeletedAtUtc", EXCLUDED."DeletedAtUtc"),
                "Preview" = NULL,
                "RecordedAtUtc" = EXCLUDED."RecordedAtUtc"
            """, cancellationToken);

        var userIds = await db.Database.SqlQuery<Guid>($"""
                DELETE FROM "DigestMessages" WHERE "ChatMessageId" = {deletion.ChatMessageId} RETURNING "UserId" AS "Value"
                """)
            .ToListAsync(cancellationToken);
        if (userIds.Count == 0) return;

        await db.RecomputeDigestsAsync(deletion.ConversationId, [.. userIds.Distinct()], options.Value, cancellationToken);
    }
}
