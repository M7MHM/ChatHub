using ChatHub.Notifications.Persistence;

namespace ChatHub.Notifications.Channels;

public interface INotificationChannel
{
    string Name { get; }

    bool CanDeliverTo(Recipient recipient);

    bool CanDeliver(Notification notification);

    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}
