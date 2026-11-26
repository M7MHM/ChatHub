using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Users.EventHandlers;

public sealed class LastSeenVisibilityChangedEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<LastSeenVisibilityChangedEventHandler> logger) : DomainEventHandler<LastSeenVisibilityChangedEvent>(logger)
{
    protected override async Task HandleAsync(LastSeenVisibilityChangedEvent domainEvent, CancellationToken cancellationToken)
    {
        var subject = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == domainEvent.UserId && !u.IsDeleted)
            .Select(u => new { u.Id, u.IsOnline, u.LastSeenUtc, u.ShowLastSeen })
            .FirstOrDefaultAsync(cancellationToken);
        if (subject is null) return;

        var viewers = (await db.PeersOfAsync(subject.Id, cancellationToken))
            .Where(p => p.SharesPresence)
            .Select(p => p.UserId)
            .ToList();
        if (viewers.Count == 0) return;

        var subjectPresence = subject.ShowLastSeen
            ? new PresenceChangedPayload(subject.Id, subject.IsOnline, subject.LastSeenUtc)
            : PresenceChangedPayload.Hidden(subject.Id);
        await signalR.SendToUsersAsync(viewers, ChatEvents.PresenceChanged, subjectPresence, cancellationToken);

        var peerPresence = await db.UserProfiles
            .AsNoTracking()
            .Where(u => viewers.Contains(u.Id))
            .Select(u => new PresenceChangedPayload(u.Id, u.IsOnline, u.LastSeenUtc))
            .ToListAsync(cancellationToken);
        foreach (var peer in peerPresence)
            await signalR.SendToUserAsync(subject.Id, ChatEvents.PresenceChanged,
                subject.ShowLastSeen ? peer : PresenceChangedPayload.Hidden(peer.UserId), cancellationToken);
    }
}
