using ChatHub.Contracts.Events;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Transactional;

public sealed class PasswordResetHandler(
    NotificationsDbContext db,
    IOptions<NotificationSettings> options,
    ILogger<PasswordResetHandler> logger)
{
    public static readonly TimeSpan PasswordChangedNoticeLifetime = TimeSpan.FromHours(24);

    public Task RequestedAsync(PasswordResetRequested request, DateTime nowUtc, CancellationToken cancellationToken) =>
        QueueAsync(OutgoingEmailTemplate.PasswordReset, request.UserId, request.Email, request.DisplayName, request.ResetLink,
            request.ExpiresAtUtc, nowUtc, cancellationToken);

    public Task CompletedAsync(PasswordResetCompleted completed, DateTime nowUtc, CancellationToken cancellationToken) =>
        QueueAsync(OutgoingEmailTemplate.PasswordChanged, completed.UserId, completed.Email, completed.DisplayName,
            options.Value.ForgotPasswordLink, completed.ChangedAtUtc + PasswordChangedNoticeLifetime, nowUtc, cancellationToken);

    private async Task QueueAsync(OutgoingEmailTemplate template, Guid userId, string email, string displayName, string link,
        DateTime expiresAtUtc, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (expiresAtUtc <= nowUtc)
        {
            logger.LogInformation("Skipping the {Template} email for user {UserId}: it expired at {ExpiresAt:O}.",
                template, userId, expiresAtUtc);
            return;
        }

        if (await db.Recipients.AnyAsync(r => r.UserId == userId && r.IsDeleted, cancellationToken))
        {
            logger.LogInformation("Skipping the {Template} email for user {UserId}: the account is deleted.", template, userId);
            return;
        }

        db.OutgoingEmails.Add(new OutgoingEmail
        {
            UserId = userId,
            Template = template,
            ToAddress = email.Trim(),
            DisplayName = displayName.Length <= Recipient.MaxDisplayNameLength
                ? displayName
                : displayName[..Recipient.MaxDisplayNameLength],
            Link = link,
            ExpiresAtUtc = expiresAtUtc,
            CreatedAtUtc = nowUtc
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }
}
