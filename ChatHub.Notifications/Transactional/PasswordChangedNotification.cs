using ChatHub.Notifications.Channels;

namespace ChatHub.Notifications.Transactional;

public sealed record PasswordChangedNotification(
    Guid UserId,
    string Email,
    string DisplayName,
    string ForgotPasswordLink) : Notification(UserId, NotificationKind.Transactional);
