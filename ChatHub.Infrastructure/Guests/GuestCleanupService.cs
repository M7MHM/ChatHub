using ChatHub.Application.Features.Guests;
using ChatHub.Application.Features.Guests.Commands;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Infrastructure.Persistence.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Guests;

public sealed class GuestCleanupService(
    IServiceScopeFactory scopeFactory,
    IDistributedLock distributedLock,
    IOptions<GuestSettings> options,
    TimeProvider timeProvider,
    ILogger<GuestCleanupService> logger) : BackgroundService
{
    public const string LockName = "guest-cleanup";
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.CleanupInterval, timeProvider);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Guest cleanup failed; it runs again on the next cycle.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<int?> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var lease = await distributedLock.TryAcquireAsync(LockName, TimeSpan.Zero, cancellationToken);
        if (lease is null)
        {
            logger.LogDebug("Another instance is cleaning up guests; skipping this cycle.");
            return null;
        }

        var deleted = 0;
        while (true)
        {
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            List<Guid> expired;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                expired = await db.UserProfiles
                    .AsNoTracking()
                    .Where(u => u.IsGuest && !u.IsDeleted && u.GuestExpiresAtUtc <= nowUtc)
                    .OrderBy(u => u.GuestExpiresAtUtc)
                    .Select(u => u.Id)
                    .Take(BatchSize)
                    .ToListAsync(cancellationToken);
            }

            var deletedInBatch = 0;
            foreach (var guestId in expired)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISender>()
                    .Send(new DeleteExpiredGuestCommand(guestId), cancellationToken);
                if (result is { IsSuccess: true, Value: true }) deletedInBatch++;
            }

            deleted += deletedInBatch;
            if (expired.Count < BatchSize || deletedInBatch == 0) break;
        }

        if (deleted > 0) logger.LogInformation("Guest cleanup deleted {Count} expired guest accounts.", deleted);
        return deleted;
    }
}
