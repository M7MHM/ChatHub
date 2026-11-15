using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Conversations.EventHandlers;

public sealed class MemberKickedEventHandler(
    ISignalRService signalR,
    ILogger<MemberKickedEventHandler> logger) : DomainEventHandler<MemberKickedEvent>(logger)
{
    protected override Task HandleAsync(MemberKickedEvent domainEvent, CancellationToken cancellationToken) =>
        signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MemberRemoved,
            new MemberRemovedPayload(domainEvent.ConversationId, domainEvent.KickedUserId, MemberRemovedPayload.Kicked),
            cancellationToken);
}
