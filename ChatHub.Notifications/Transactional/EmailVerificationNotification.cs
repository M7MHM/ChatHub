using ChatHub.Notifications.Channels;

namespace ChatHub.Notifications.Transactional;

public sealed record EmailVerificationNotification(
    Guid UserId,
    string Email,
    string DisplayName,
    string VerificationLink,
    DateTime ExpiresAtUtc) : Notification(UserId, NotificationKind.Transactional);
