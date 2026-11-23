using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Digests;

public sealed record RecipientState(
    Guid UserId,
    string? Email,
    string DisplayName,
    bool EmailVerified,
    bool EmailNotificationsEnabled,
    string? UnsubscribeLink,
    DateTime OccurredAtUtc);

public sealed class UserEventsHandler(NotificationsDbContext db)
{
    public Task UpsertAsync(RecipientState state, CancellationToken cancellationToken)
    {
        var address = string.IsNullOrWhiteSpace(state.Email) || state.Email.Length > Recipient.MaxEmailLength ? null : state.Email.Trim();
        var name = state.DisplayName.Length <= Recipient.MaxDisplayNameLength
            ? state.DisplayName
            : state.DisplayName[..Recipient.MaxDisplayNameLength];
        var link = string.IsNullOrWhiteSpace(state.UnsubscribeLink) || state.UnsubscribeLink.Length > Recipient.MaxLinkLength
            ? null
            : state.UnsubscribeLink;
        DateTime? linkIssuedAtUtc = link is null ? null : state.OccurredAtUtc;

        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Recipients" AS r ("UserId", "Email", "DisplayName", "IsDeleted", "EmailVerified", "EmailNotificationsEnabled",
                "UnsubscribeLink", "UnsubscribeLinkIssuedAtUtc", "LastEventAtUtc")
            VALUES ({state.UserId}, CAST({address} AS text), {name}, false, {state.EmailVerified}, {state.EmailNotificationsEnabled},
                CAST({link} AS text), {linkIssuedAtUtc}, {state.OccurredAtUtc})
            ON CONFLICT ("UserId") DO UPDATE SET
                "Email" = CASE WHEN r."LastEventAtUtc" < EXCLUDED."LastEventAtUtc" THEN EXCLUDED."Email" ELSE r."Email" END,
                "DisplayName" = CASE WHEN r."LastEventAtUtc" < EXCLUDED."LastEventAtUtc"
                                     THEN EXCLUDED."DisplayName" ELSE r."DisplayName" END,
                "EmailVerified" = CASE WHEN r."LastEventAtUtc" < EXCLUDED."LastEventAtUtc"
                                       THEN EXCLUDED."EmailVerified" ELSE r."EmailVerified" END,
                "EmailNotificationsEnabled" = CASE WHEN r."LastEventAtUtc" < EXCLUDED."LastEventAtUtc"
                                                   THEN EXCLUDED."EmailNotificationsEnabled" ELSE r."EmailNotificationsEnabled" END,
                "UnsubscribeLink" = CASE WHEN EXCLUDED."UnsubscribeLink" IS NOT NULL AND (r."UnsubscribeLinkIssuedAtUtc" IS NULL
                                              OR r."UnsubscribeLinkIssuedAtUtc" < EXCLUDED."UnsubscribeLinkIssuedAtUtc")
                                         THEN EXCLUDED."UnsubscribeLink" ELSE r."UnsubscribeLink" END,
                "UnsubscribeLinkIssuedAtUtc" = CASE WHEN EXCLUDED."UnsubscribeLink" IS NOT NULL AND (r."UnsubscribeLinkIssuedAtUtc" IS NULL
                                                         OR r."UnsubscribeLinkIssuedAtUtc" < EXCLUDED."UnsubscribeLinkIssuedAtUtc")
                                                    THEN EXCLUDED."UnsubscribeLinkIssuedAtUtc" ELSE r."UnsubscribeLinkIssuedAtUtc" END,
                "LastEventAtUtc" = GREATEST(r."LastEventAtUtc", EXCLUDED."LastEventAtUtc")
            WHERE NOT r."IsDeleted"
            """, cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Recipients" ("UserId", "Email", "DisplayName", "IsDeleted", "EmailVerified", "EmailNotificationsEnabled",
                "UnsubscribeLink", "UnsubscribeLinkIssuedAtUtc", "LastEventAtUtc")
            VALUES ({userId}, NULL, '', true, false, false, NULL, NULL, {occurredAtUtc})
            ON CONFLICT ("UserId") DO UPDATE
                SET "IsDeleted" = true, "Email" = NULL, "DisplayName" = '', "EmailVerified" = false,
                    "EmailNotificationsEnabled" = false, "UnsubscribeLink" = NULL, "UnsubscribeLinkIssuedAtUtc" = NULL,
                    "LastEventAtUtc" = GREATEST("Recipients"."LastEventAtUtc", EXCLUDED."LastEventAtUtc")
            """, cancellationToken);

        await db.DigestMessages.Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.PendingDigests.Where(d => d.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.DigestCooldowns.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await db.ConversationReads.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }
}
