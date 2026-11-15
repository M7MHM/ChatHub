using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Messages.EventHandlers;

public sealed class MessageDeletedEventHandler(
    ISignalRService signalR,
    ILogger<MessageDeletedEventHandler> logger) : DomainEventHandler<MessageDeletedEvent>(logger)
{
    protected override Task HandleAsync(MessageDeletedEvent domainEvent, CancellationToken cancellationToken) =>
        signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MessageDeleted,
            new MessageDeletedPayload(domainEvent.ConversationId, domainEvent.MessageId), cancellationToken);
}

public sealed record MessageDeletedPayload(Guid ConversationId, Guid MessageId);
