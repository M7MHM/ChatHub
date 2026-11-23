using ChatHub.Contracts.Events;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Digests;

public sealed class PresenceHandler(NotificationsDbContext db, IOptions<NotificationSettings> options)
{
    public async Task HandleAsync(UserStatusChanged change, CancellationToken cancellationToken)
    {
        var applied = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "RecipientPresence" AS p ("UserId", "IsOnline", "ChangedAtUtc")
            VALUES ({change.UserId}, {change.IsOnline}, {change.OccurredAtUtc})
            ON CONFLICT ("UserId") DO UPDATE SET "IsOnline" = EXCLUDED."IsOnline", "ChangedAtUtc" = EXCLUDED."ChangedAtUtc"
            WHERE p."ChangedAtUtc" < EXCLUDED."ChangedAtUtc"
            """, cancellationToken);
        if (applied == 0 || change.IsOnline) return;

        var dueAtUtc = change.OccurredAtUtc + options.Value.DigestDelay;
        await db.PendingDigests
            .Where(d => d.UserId == change.UserId && d.DueAtUtc < dueAtUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DueAtUtc, dueAtUtc), cancellationToken);
    }
}
