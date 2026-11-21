using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.RealTime;

public sealed class RedisPresenceMaintenance(
    RedisUserConnectionTracker tracker,
    PresenceSynchronizer presence,
    ServerInstance instance,
    IOptions<RealTimeSettings> options,
    TimeProvider timeProvider,
    ILogger<RedisPresenceMaintenance> logger) : BackgroundService
{
    private readonly HashSet<Guid> _usersToSync = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Instance {InstanceId} tracks its SignalR connections in Redis.", instance.Id);
        try
        {
            using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, timeProvider);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await tracker.RetryPendingRemovalsAsync(_usersToSync);
            await tracker.UnregisterInstanceAsync(_usersToSync);
            await SyncPresenceAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not remove instance {InstanceId} from Redis on shutdown; other instances sweep it once its heartbeat expires.",
                instance.Id);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await tracker.SubscribeAsync();
            await tracker.HeartbeatAsync(_usersToSync);
            await tracker.RetryPendingRemovalsAsync(_usersToSync);
            await tracker.SweepAsync(_usersToSync);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Redis presence maintenance failed; it runs again in {Interval}.", options.Value.HeartbeatInterval);
        }

        try
        {
            await SyncPresenceAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not update the presence of {Count} users; retrying in {Interval}.",
                _usersToSync.Count, options.Value.HeartbeatInterval);
        }
    }

    private async Task SyncPresenceAsync(CancellationToken cancellationToken)
    {
        foreach (var userId in _usersToSync.ToList())
        {
            await presence.SyncAsync(userId, cancellationToken);
            _usersToSync.Remove(userId);
        }
    }
}
