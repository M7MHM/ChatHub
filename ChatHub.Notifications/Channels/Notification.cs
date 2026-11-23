namespace ChatHub.Notifications.Channels;

public enum NotificationKind
{
    Transactional,
    Digest
}

public abstract record Notification(Guid UserId, NotificationKind Kind);
