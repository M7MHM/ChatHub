using ChatHub.Contracts.Events;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Digests;

public sealed class ConversationReadHandler(NotificationsDbContext db, IOptions<NotificationSettings> options)
{
    public async Task HandleAsync(ConversationRead read, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "ConversationReads" ("UserId", "ConversationId", "ReadUpToUtc")
            VALUES ({read.UserId}, {read.ConversationId}, {read.ReadUpToUtc})
            ON CONFLICT ("UserId", "ConversationId") DO UPDATE
                SET "ReadUpToUtc" = GREATEST("ConversationReads"."ReadUpToUtc", EXCLUDED."ReadUpToUtc")
            """, cancellationToken);

        await db.RemoveMessagesUpToAsync(read.UserId, read.ConversationId, read.ReadUpToUtc, options.Value,
            resetDelivery: false, cancellationToken);
    }
}
