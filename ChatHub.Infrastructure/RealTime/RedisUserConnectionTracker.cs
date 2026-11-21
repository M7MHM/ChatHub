using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatHub.Infrastructure.RealTime;

public sealed class RedisUserConnectionTracker(
    IConnectionMultiplexer redis,
    ServerInstance instance,
    IOptions<RealTimeSettings> settings,
    ILogger<RedisUserConnectionTracker> logger) : IUserConnectionTracker
{
    public const string KeyPrefix = "chathub:presence:";
    public static readonly RedisKey InstancesKey = KeyPrefix + "instances";
    public static readonly RedisChannel DisconnectChannel = RedisChannel.Literal("presence:disconnect");

    private const string AddScript = """
        local isFirst = redis.call('HLEN', KEYS[1]) == 0
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('SADD', KEYS[2], ARGV[3])
        redis.call('SADD', KEYS[3], ARGV[2])
        if isFirst then return 1 end
        return 0
        """;

    private const string RemoveScript = """
        if #KEYS == 3 and redis.call('EXISTS', KEYS[3]) == 1 then return -1 end
        redis.call('SREM', KEYS[2], ARGV[2])
        if redis.call('HDEL', KEYS[1], ARGV[1]) == 0 then return 0 end
        if redis.call('HLEN', KEYS[1]) == 0 then return 2 end
        return 1
        """;

    private const string HeartbeatScript = """
        local existed = redis.call('EXISTS', KEYS[1])
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
        redis.call('SADD', KEYS[2], ARGV[1])
        return existed
        """;

    private const string ForgetInstanceScript = """
        if redis.call('EXISTS', KEYS[1]) == 1 or redis.call('SCARD', KEYS[2]) > 0 then return 0 end
        redis.call('SREM', KEYS[3], ARGV[1])
        return 1
        """;

    private readonly InMemoryUserConnectionTracker _local = new();
    private readonly ConcurrentQueue<(Guid UserId, string ConnectionId)> _pendingRemovals = new();
    private bool _subscribed;
    private bool _registrationLost = true;

    private IDatabase Database => redis.GetDatabase();

    public static RedisKey UserKey(Guid userId) => $"{KeyPrefix}user:{userId}";

    public static RedisKey ConnectionsKey(string instanceId) => $"{KeyPrefix}instance:{instanceId}:connections";

    public static RedisKey HeartbeatKey(string instanceId) => $"{KeyPrefix}instance:{instanceId}:heartbeat";

    public async Task<bool> AddAsync(Guid userId, HubCallerContext connection, CancellationToken cancellationToken = default)
    {
        _local.Add(userId, connection);
        try
        {
            return await RegisterAsync(userId, connection.ConnectionId);
        }
        catch
        {
            _local.Remove(userId, connection.ConnectionId);
            throw;
        }
    }

    public async Task<bool> RemoveAsync(Guid userId, string connectionId, CancellationToken cancellationToken = default)
    {
        _local.Remove(userId, connectionId);
        try
        {
            return await UnregisterAsync(userId, connectionId, instance.Id, onlyIfOwnerIsDead: false) == Removal.RemovedLast;
        }
        catch (Exception exception)
        {
            _pendingRemovals.Enqueue((userId, connectionId));
            logger.LogWarning(exception, "Could not remove connection {ConnectionId} of user {UserId} from Redis; retrying on the next heartbeat.",
                connectionId, userId);
            return false;
        }
    }

    public async Task<bool> IsOnlineAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await Database.HashLengthAsync(UserKey(userId)) > 0;

    public async Task<IReadOnlyList<string>> GetConnectionIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var entries = await Database.HashGetAllAsync(UserKey(userId));
        if (entries.Length == 0) return [];

        var local = _local.GetConnections(userId).ToHashSet();
        var aliveOwners = new HashSet<string>();
        foreach (var owner in entries.Select(e => e.Value.ToString()).Where(owner => owner != instance.Id).Distinct())
            if (await Database.KeyExistsAsync(HeartbeatKey(owner)))
                aliveOwners.Add(owner);

        return entries
            .Where(e => e.Value == instance.Id ? local.Contains(e.Name.ToString()) : aliveOwners.Contains(e.Value.ToString()))
            .Select(e => e.Name.ToString())
            .ToList();
    }

    public async Task DisconnectAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _local.Abort(userId);
        try
        {
            await redis.GetSubscriber().PublishAsync(DisconnectChannel, userId.ToString());
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not broadcast the disconnect of user {UserId}; their connections on other instances stay open.",
                userId);
        }
    }

    public async Task SubscribeAsync()
    {
        if (_subscribed) return;

        await redis.GetSubscriber().SubscribeAsync(DisconnectChannel, (_, message) =>
        {
            if (Guid.TryParse(message.ToString(), out var userId)) _local.Abort(userId);
        });
        _subscribed = true;
    }

    public async Task HeartbeatAsync(ISet<Guid> usersToSync)
    {
        var existed = (int)await Database.ScriptEvaluateAsync(HeartbeatScript,
            [HeartbeatKey(instance.Id), InstancesKey],
            [instance.Id, (long)settings.Value.InstanceTimeout.TotalMilliseconds]) == 1;
        if (existed && !_registrationLost) return;

        _registrationLost = true;
        var connections = _local.GetAll();
        foreach (var (userId, connection) in connections)
            if (await RegisterAsync(userId, connection.ConnectionId))
                usersToSync.Add(userId);
        _registrationLost = false;

        if (!existed && connections.Count > 0)
            logger.LogWarning("The heartbeat of instance {InstanceId} had expired; re-registered its {Count} live connections.",
                instance.Id, connections.Count);
    }

    public async Task RetryPendingRemovalsAsync(ISet<Guid> usersToSync)
    {
        var count = _pendingRemovals.Count;
        while (count-- > 0 && _pendingRemovals.TryDequeue(out var removal))
        {
            try
            {
                await UnregisterAsync(removal.UserId, removal.ConnectionId, instance.Id, onlyIfOwnerIsDead: false);
            }
            catch
            {
                _pendingRemovals.Enqueue(removal);
                throw;
            }

            usersToSync.Add(removal.UserId);
        }
    }

    public async Task SweepAsync(ISet<Guid> usersToSync)
    {
        foreach (var ownerId in (await Database.SetMembersAsync(InstancesKey)).Select(v => v.ToString()))
        {
            if (ownerId == instance.Id || await Database.KeyExistsAsync(HeartbeatKey(ownerId))) continue;

            var removed = 0;
            var ownerCameBack = false;
            foreach (var member in await Database.SetMembersAsync(ConnectionsKey(ownerId)))
            {
                if (!TryParseMember(member, out var userId, out var connectionId)) continue;

                var removal = await UnregisterAsync(userId, connectionId, ownerId, onlyIfOwnerIsDead: true);
                if (removal == Removal.OwnerAlive)
                {
                    ownerCameBack = true;
                    break;
                }

                if (removal is Removal.Removed or Removal.RemovedLast) removed++;
                if (removal == Removal.RemovedLast) usersToSync.Add(userId);
            }

            if (removed > 0)
                logger.LogWarning("Instance {OwnerId} stopped sending heartbeats; removed {Count} of its connections.", ownerId, removed);
            if (!ownerCameBack)
                await ForgetInstanceAsync(ownerId);
        }
    }

    public async Task UnregisterInstanceAsync(ISet<Guid> usersToSync)
    {
        foreach (var member in await Database.SetMembersAsync(ConnectionsKey(instance.Id)))
            if (TryParseMember(member, out var userId, out var connectionId) &&
                await UnregisterAsync(userId, connectionId, instance.Id, onlyIfOwnerIsDead: false) == Removal.RemovedLast)
                usersToSync.Add(userId);

        await Database.KeyDeleteAsync(HeartbeatKey(instance.Id));
        await ForgetInstanceAsync(instance.Id);
    }

    private async Task<bool> RegisterAsync(Guid userId, string connectionId) =>
        (int)await Database.ScriptEvaluateAsync(AddScript,
            [UserKey(userId), ConnectionsKey(instance.Id), InstancesKey],
            [connectionId, instance.Id, Member(userId, connectionId)]) == 1;

    private async Task<Removal> UnregisterAsync(Guid userId, string connectionId, string ownerId, bool onlyIfOwnerIsDead)
    {
        RedisKey[] keys = onlyIfOwnerIsDead
            ? [UserKey(userId), ConnectionsKey(ownerId), HeartbeatKey(ownerId)]
            : [UserKey(userId), ConnectionsKey(ownerId)];
        return (Removal)(int)await Database.ScriptEvaluateAsync(RemoveScript, keys, [connectionId, Member(userId, connectionId)]);
    }

    private Task ForgetInstanceAsync(string instanceId) =>
        Database.ScriptEvaluateAsync(ForgetInstanceScript,
            [HeartbeatKey(instanceId), ConnectionsKey(instanceId), InstancesKey], [instanceId]);

    private static RedisValue Member(Guid userId, string connectionId) => $"{userId}:{connectionId}";

    private static bool TryParseMember(RedisValue member, out Guid userId, out string connectionId)
    {
        var value = member.ToString();
        var separator = value.IndexOf(':');
        connectionId = separator < 0 ? string.Empty : value[(separator + 1)..];
        return Guid.TryParse(separator < 0 ? value : value[..separator], out userId) && connectionId.Length > 0;
    }

    private enum Removal
    {
        OwnerAlive = -1,
        NotFound = 0,
        Removed = 1,
        RemovedLast = 2
    }
}
