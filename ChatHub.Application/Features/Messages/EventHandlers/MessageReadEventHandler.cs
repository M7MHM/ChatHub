using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Messages.EventHandlers;

public sealed class MessageReadEventHandler(
    ISignalRService signalR,
    ILogger<MessageReadEventHandler> logger) : DomainEventHandler<MessageReadEvent>(logger)
{
    protected override Task HandleAsync(MessageReadEvent domainEvent, CancellationToken cancellationToken) =>
        signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MessagesRead,
            new MessagesReadPayload(domainEvent.ConversationId, domainEvent.UserId, domainEvent.MessageId,
                domainEvent.ReadAtUtc), cancellationToken);
}

public sealed record MessagesReadPayload(Guid ConversationId, Guid UserId, Guid MessageId, DateTime ReadAtUtc);
