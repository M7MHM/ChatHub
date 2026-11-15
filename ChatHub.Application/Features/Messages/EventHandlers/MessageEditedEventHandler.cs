using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Messages.EventHandlers;

public sealed class MessageEditedEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<MessageEditedEventHandler> logger) : DomainEventHandler<MessageEditedEvent>(logger)
{
    protected override async Task HandleAsync(MessageEditedEvent domainEvent, CancellationToken cancellationToken)
    {
        var message = await db.LoadForConversationAsync(domainEvent.MessageId, domainEvent.ConversationId, cancellationToken);
        if (message is null) return;

        await signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MessageUpdated, message, cancellationToken);
    }
}
