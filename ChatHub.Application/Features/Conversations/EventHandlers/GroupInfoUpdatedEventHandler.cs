using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Conversations.EventHandlers;

public sealed class GroupInfoUpdatedEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<GroupInfoUpdatedEventHandler> logger) : DomainEventHandler<GroupInfoUpdatedEvent>(logger)
{
    protected override async Task HandleAsync(GroupInfoUpdatedEvent domainEvent, CancellationToken cancellationToken)
    {
        var payload = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == domainEvent.ConversationId)
            .Select(c => new ConversationUpdatedPayload(c.Id, c.Title, c.ImageUrl))
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null) return;

        await signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.ConversationUpdated, payload, cancellationToken);
    }
}

public sealed record ConversationUpdatedPayload(Guid ConversationId, string? Title, string? ImageUrl);
