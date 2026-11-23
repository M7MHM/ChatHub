using ChatHub.Contracts.Events;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Transactional;

public sealed class EmailVerificationHandler(NotificationsDbContext db, ILogger<EmailVerificationHandler> logger)
{
    public async Task HandleAsync(EmailVerificationRequested request, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (request.ExpiresAtUtc <= nowUtc)
        {
            logger.LogInformation("Skipping the verification email for user {UserId}: its link expired at {ExpiresAt:O}.",
                request.UserId, request.ExpiresAtUtc);
            return;
        }

        if (await db.Recipients.AnyAsync(r => r.UserId == request.UserId && r.IsDeleted, cancellationToken))
        {
            logger.LogInformation("Skipping the verification email for user {UserId}: the account is deleted.", request.UserId);
            return;
        }

        db.OutgoingEmails.Add(new OutgoingEmail
        {
            UserId = request.UserId,
            Template = OutgoingEmailTemplate.EmailVerification,
            ToAddress = request.Email.Trim(),
            DisplayName = request.DisplayName.Length <= Recipient.MaxDisplayNameLength
                ? request.DisplayName
                : request.DisplayName[..Recipient.MaxDisplayNameLength],
            Link = request.VerificationLink,
            ExpiresAtUtc = request.ExpiresAtUtc,
            CreatedAtUtc = nowUtc
        });
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }
}
