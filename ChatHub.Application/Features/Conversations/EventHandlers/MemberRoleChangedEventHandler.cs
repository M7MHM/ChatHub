using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Conversations.EventHandlers;

public sealed class MemberRoleChangedEventHandler(
    ISignalRService signalR,
    ILogger<MemberRoleChangedEventHandler> logger) : DomainEventHandler<MemberRoleChangedEvent>(logger)
{
    protected override Task HandleAsync(MemberRoleChangedEvent domainEvent, CancellationToken cancellationToken) =>
        signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MemberRoleChanged,
            new MemberRoleChangedPayload(domainEvent.ConversationId, domainEvent.UserId, domainEvent.NewRole),
            cancellationToken);
}

public sealed record MemberRoleChangedPayload(Guid ConversationId, Guid UserId, GroupRole Role);
