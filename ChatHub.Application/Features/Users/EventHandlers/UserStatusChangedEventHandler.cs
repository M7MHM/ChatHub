using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Users.EventHandlers;

public sealed class UserStatusChangedEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<UserStatusChangedEventHandler> logger) : DomainEventHandler<UserStatusChangedEvent>(logger)
{
    protected override async Task HandleAsync(UserStatusChangedEvent domainEvent, CancellationToken cancellationToken)
    {
        var peerIds = await db.PeerIdsOfAsync(domainEvent.UserId, cancellationToken);
        if (peerIds.Count == 0) return;

        var isDeleted = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == domainEvent.UserId)
            .Select(u => u.IsDeleted)
            .FirstOrDefaultAsync(cancellationToken);

        var payload = isDeleted
            ? new PresenceChangedPayload(domainEvent.UserId, false, null)
            : new PresenceChangedPayload(domainEvent.UserId, domainEvent.IsOnline, domainEvent.LastSeenUtc);

        await signalR.SendToUsersAsync(peerIds, ChatEvents.PresenceChanged, payload, cancellationToken);
    }
}

public sealed record PresenceChangedPayload(Guid UserId, bool IsOnline, DateTime? LastSeenUtc);
