using ChatHub.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Infrastructure.RealTime;

public sealed class PresenceSynchronizer(IServiceScopeFactory scopeFactory, IUserConnectionTracker tracker)
{
    private const int MaxAttempts = 3;

    public async Task SyncAsync(Guid userId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
            if (user is null) return;

            var isOnline = await tracker.IsOnlineAsync(userId, cancellationToken);
            if (user.IsOnline == isOnline) return;

            if (isOnline) user.GoOnline();
            else user.GoOffline();

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
