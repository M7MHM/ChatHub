using ChatHub.Application.Common.Interfaces;
using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Storage;

public sealed record FileCleanupResult(int Orphaned, int Deleted);

public sealed class FileCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<FileStorageSettings> options,
    TimeProvider timeProvider,
    ILogger<FileCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);
    private const int BatchSize = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.CleanupIntervalMinutes), timeProvider);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "File cleanup failed; it runs again on the next cycle.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<FileCleanupResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var orphanCutoff = timeProvider.GetUtcNow().UtcDateTime - OrphanAge;
        int orphaned = 0, deleted = 0;

        while (true)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();

            var batch = await db.UploadedFiles
                .Where(f => f.IsDeleted || (f.AttachedAtUtc == null && f.CreatedAtUtc < orphanCutoff))
                .OrderBy(f => f.CreatedAtUtc)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            foreach (var file in batch)
            {
                db.UploadedFiles.Remove(file);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    db.Entry(file).State = EntityState.Detached;
                    continue;
                }

                await storage.DeleteAsync(file.StorageKey, cancellationToken);
                if (file.IsDeleted) deleted++;
                else orphaned++;
            }

            if (batch.Count < BatchSize) break;
        }

        logger.LogInformation("File cleanup removed {Orphaned} orphaned and {Deleted} deleted files.", orphaned, deleted);
        return new FileCleanupResult(orphaned, deleted);
    }
}
