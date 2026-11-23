using ChatHub.Contracts.Events;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Digests;

public sealed class ConversationReadHandler(NotificationsDbContext db, IOptions<NotificationSettings> options)
{
    public Task HandleAsync(ConversationRead read, CancellationToken cancellationToken) =>
        MarkReadUpToAsync(read.UserId, read.ConversationId, read.ReadUpToUtc, cancellationToken);

    public async Task MarkReadUpToAsync(Guid userId, Guid conversationId, DateTime upToUtc, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ConversationReads" ("UserId", "ConversationId", "ReadUpToUtc")
            VALUES ({userId}, {conversationId}, {upToUtc})
            ON CONFLICT ("UserId", "ConversationId") DO UPDATE
                SET "ReadUpToUtc" = GREATEST("ConversationReads"."ReadUpToUtc", EXCLUDED."ReadUpToUtc")
            """, cancellationToken);

        await db.RemoveMessagesUpToAsync(userId, conversationId, upToUtc, options.Value, resetDelivery: false, cancellationToken);
    }
}
