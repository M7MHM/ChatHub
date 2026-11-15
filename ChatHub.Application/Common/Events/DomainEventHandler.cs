using ChatHub.Application.Common.Models;
using ChatHub.Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Common.Events;

public abstract class DomainEventHandler<TEvent>(ILogger logger) : INotificationHandler<DomainEventNotification<TEvent>>
    where TEvent : IDomainEvent
{
    public async Task Handle(DomainEventNotification<TEvent> notification, CancellationToken cancellationToken)
    {
        try
        {
            await HandleAsync(notification.DomainEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handling {EventType} failed: {@Event}", typeof(TEvent).Name, notification.DomainEvent);
        }
    }

    protected abstract Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
