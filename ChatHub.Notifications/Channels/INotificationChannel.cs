using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;

namespace ChatHub.Notifications.Channels;

public interface INotificationChannel
{
    string Name { get; }

    bool CanDeliverTo(Recipient recipient);

    Task SendAsync(DigestNotification notification, CancellationToken cancellationToken);
}
