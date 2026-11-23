using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Transactional;

public sealed class EmailVerificationHandler(
    NotificationsDbContext db,
    IEnumerable<INotificationChannel> channels,
    ILogger<EmailVerificationHandler> logger)
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

        var notification = new EmailVerificationNotification(request.UserId, request.Email, request.DisplayName,
            request.VerificationLink, request.ExpiresAtUtc);
        var deliverable = channels.Where(c => c.CanDeliver(notification)).ToList();
        if (deliverable.Count == 0)
        {
            logger.LogWarning("No channel can send the verification email for user {UserId}.", request.UserId);
            return;
        }

        foreach (var channel in deliverable)
            await channel.SendAsync(notification, cancellationToken);

        db.SentNotifications.AddRange(deliverable.Select(channel => new SentNotification
        {
            UserId = request.UserId,
            Channel = channel.Name,
            Kind = NotificationKind.Transactional,
            SentAtUtc = nowUtc
        }));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();

        logger.LogInformation("Sent the verification email to user {UserId} via {Channels}.",
            request.UserId, string.Join(", ", deliverable.Select(c => c.Name)));
    }
}
