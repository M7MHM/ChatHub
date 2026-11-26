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
        var subject = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == domainEvent.UserId)
            .Select(u => new { u.IsDeleted, u.ShowLastSeen })
            .FirstOrDefaultAsync(cancellationToken);
        if (subject is null || (!subject.IsDeleted && !subject.ShowLastSeen)) return;

        var viewers = (await db.PeersOfAsync(domainEvent.UserId, cancellationToken))
            .Where(p => p.SharesPresence)
            .Select(p => p.UserId)
            .ToList();
        if (viewers.Count == 0) return;

        var payload = subject.IsDeleted
            ? PresenceChangedPayload.Hidden(domainEvent.UserId)
            : new PresenceChangedPayload(domainEvent.UserId, domainEvent.IsOnline, domainEvent.LastSeenUtc);

        await signalR.SendToUsersAsync(viewers, ChatEvents.PresenceChanged, payload, cancellationToken);
    }
}

public sealed record PresenceChangedPayload(Guid UserId, bool IsOnline, DateTime? LastSeenUtc)
{
    public static PresenceChangedPayload Hidden(Guid userId) => new(userId, false, null);
}
