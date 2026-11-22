using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Digests;

public sealed class UserEventsHandler(NotificationsDbContext db)
{
    public Task UpsertAsync(Guid userId, string? email, string displayName, DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var address = string.IsNullOrWhiteSpace(email) || email.Length > Recipient.MaxEmailLength ? null : email.Trim();
        var name = displayName.Length <= Recipient.MaxDisplayNameLength ? displayName : displayName[..Recipient.MaxDisplayNameLength];

        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Recipients" ("UserId", "Email", "DisplayName", "IsDeleted", "LastEventAtUtc")
            VALUES ({userId}, CAST({address} AS text), {name}, false, {occurredAtUtc})
            ON CONFLICT ("UserId") DO UPDATE
                SET "Email" = EXCLUDED."Email", "DisplayName" = EXCLUDED."DisplayName", "LastEventAtUtc" = EXCLUDED."LastEventAtUtc"
                WHERE NOT "Recipients"."IsDeleted" AND "Recipients"."LastEventAtUtc" < EXCLUDED."LastEventAtUtc"
            """, cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Recipients" ("UserId", "Email", "DisplayName", "IsDeleted", "LastEventAtUtc")
            VALUES ({userId}, NULL, '', true, {occurredAtUtc})
            ON CONFLICT ("UserId") DO UPDATE
                SET "IsDeleted" = true, "Email" = NULL, "DisplayName" = '',
                    "LastEventAtUtc" = GREATEST("Recipients"."LastEventAtUtc", EXCLUDED."LastEventAtUtc")
            """, cancellationToken);

        await db.DigestMessages.Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.PendingDigests.Where(d => d.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.DigestCooldowns.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.ConversationReads.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }
}
