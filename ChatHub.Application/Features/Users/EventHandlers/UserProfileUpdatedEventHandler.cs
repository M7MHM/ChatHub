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
        var peers = await db.PeersOfAsync(domainEvent.UserId, cancellationToken);
        if (peers.Count == 0) return;

        var user = await db.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == domainEvent.UserId, cancellationToken);
        if (user is null) return;

        foreach (var group in peers.GroupBy(p => p.SharesPresence && user.ShowLastSeen))
        {
            var summary = UserSummary.Create(user.Id, user.Username, user.DisplayName, user.AvatarUrl, user.IsOnline,
                user.LastSeenUtc, user.IsDeleted, presenceVisible: group.Key);
            await signalR.SendToUsersAsync(group.Select(p => p.UserId), ChatEvents.ProfileUpdated, summary, cancellationToken);
        }
    }
}
