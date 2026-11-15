using ChatHub.Application.Common.Events;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChatHub.Application.Features.Users.EventHandlers;

public sealed class UserProfileUpdatedEventHandler(
    IApplicationDbContext db,
    ISignalRService signalR,
    ILogger<UserProfileUpdatedEventHandler> logger) : DomainEventHandler<UserProfileUpdatedEvent>(logger)
{
    protected override async Task HandleAsync(UserProfileUpdatedEvent domainEvent, CancellationToken cancellationToken)
    {
        var peerIds = await db.PeerIdsOfAsync(domainEvent.UserId, cancellationToken);
        if (peerIds.Count == 0) return;

        var summary = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id == domainEvent.UserId)
            .Select(UserSummary.Projection)
            .FirstOrDefaultAsync(cancellationToken);
        if (summary is null) return;

        await signalR.SendToUsersAsync(peerIds, ChatEvents.ProfileUpdated, summary, cancellationToken);
    }
}
