using ChatHub.Notifications.Channels;

namespace ChatHub.Notifications.Transactional;

public sealed record PasswordResetNotification(
    Guid UserId,
    string Email,
    string DisplayName,
    string ResetLink,
    DateTime ExpiresAtUtc) : Notification(UserId, NotificationKind.Transactional);
