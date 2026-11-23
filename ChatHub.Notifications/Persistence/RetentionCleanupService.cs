using ChatHub.Notifications.Digests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Persistence;

public sealed record CleanupResult(int ProcessedMessages, int SentNotifications, int ConversationReads, int Cooldowns,
    int MessageSnapshots = 0, int FailedEmails = 0);

public sealed class RetentionCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationSettings> options,
    TimeProvider timeProvider,
    ILogger<RetentionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, timeProvider);
            do
            {
                try
                {
                    await CleanupAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, "Notification cleanup failed; it runs again in {Interval}.", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var cutoff = nowUtc - settings.InboxRetention;
        var cooldownCutoff = nowUtc - settings.Cooldown;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var result = new CleanupResult(
            await db.ProcessedMessages.Where(m => m.ProcessedAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken),
            await db.SentNotifications.Where(s => s.SentAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken),
            await db.ConversationReads.Where(r => r.ReadUpToUtc < cutoff).ExecuteDeleteAsync(cancellationToken),
            await db.DigestCooldowns.Where(c => c.LastSentAtUtc < cooldownCutoff).ExecuteDeleteAsync(cancellationToken),
            await db.MessageSnapshots.Where(s => s.RecordedAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken),
            await db.OutgoingEmails.Where(e => e.FailedAtUtc < cutoff).ExecuteDeleteAsync(cancellationToken));

        if (result != new CleanupResult(0, 0, 0, 0))
            logger.LogInformation("Notification cleanup deleted {Result}.", result);
        return result;
    }
}
