using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Conversations.EventHandlers;

public sealed class MemberLeftEventHandler(
    ISignalRService signalR,
    ILogger<MemberLeftEventHandler> logger) : DomainEventHandler<MemberLeftEvent>(logger)
{
    protected override Task HandleAsync(MemberLeftEvent domainEvent, CancellationToken cancellationToken) =>
        signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MemberRemoved,
            new MemberRemovedPayload(domainEvent.ConversationId, domainEvent.UserId, MemberRemovedPayload.Left),
            cancellationToken);
}
