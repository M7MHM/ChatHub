using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Messages.EventHandlers;

public sealed class MessageDeliveredEventHandler(
    ISignalRService signalR,
    ILogger<MessageDeliveredEventHandler> logger) : DomainEventHandler<MessageDeliveredEvent>(logger)
{
    protected override Task HandleAsync(MessageDeliveredEvent domainEvent, CancellationToken cancellationToken) =>
        signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MessagesDelivered,
            new MessagesDeliveredPayload(domainEvent.ConversationId, domainEvent.UserId, domainEvent.MessageId),
            cancellationToken);
}

public sealed record MessagesDeliveredPayload(Guid ConversationId, Guid UserId, Guid MessageId);
