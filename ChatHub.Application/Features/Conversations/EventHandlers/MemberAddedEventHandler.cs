using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Conversations.EventHandlers;

public sealed class MemberAddedEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<MemberAddedEventHandler> logger) : DomainEventHandler<MemberAddedEvent>(logger)
{
    protected override async Task HandleAsync(MemberAddedEvent domainEvent, CancellationToken cancellationToken)
    {
        var payload = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == domainEvent.ConversationId && m.UserId == domainEvent.UserId && m.IsActive)
            .Select(m => new MemberAddedPayload(
                m.ConversationId,
                UserSummary.Create(m.User.Id, m.User.Username, m.User.DisplayName, m.User.AvatarUrl, m.User.IsOnline,
                    m.User.LastSeenUtc, m.User.IsDeleted, presenceVisible: false),
                m.Role))
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null) return;

        // The added user joins the SignalR group only after the save, so they get the conversation directly.
        await signalR.SendToConversationAsync(domainEvent.ConversationId, ChatEvents.MemberAdded, payload, cancellationToken);

        var items = await db.ForUserAsync(domainEvent.UserId, [domainEvent.ConversationId], cancellationToken);
        if (items is [var item])
            await signalR.SendToUserAsync(domainEvent.UserId, ChatEvents.ConversationCreated, item, cancellationToken);
    }
}

public sealed record MemberAddedPayload(Guid ConversationId, UserSummary Member, GroupRole Role);
