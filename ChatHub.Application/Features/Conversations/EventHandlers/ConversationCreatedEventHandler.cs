using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Conversations.EventHandlers;

public sealed class ConversationCreatedEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<ConversationCreatedEventHandler> logger) : DomainEventHandler<ConversationCreatedEvent>(logger)
{
    protected override async Task HandleAsync(ConversationCreatedEvent domainEvent, CancellationToken cancellationToken)
    {
        foreach (var userId in domainEvent.MemberIds.Distinct())
        {
            var items = await db.ForUserAsync(userId, [domainEvent.ConversationId], cancellationToken);
            if (items is [var item])
                await signalR.SendToUserAsync(userId, ChatEvents.ConversationCreated, item, cancellationToken);
        }
    }
}
