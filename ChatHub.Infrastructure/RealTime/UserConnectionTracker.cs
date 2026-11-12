using System.Collections.Concurrent;

namespace ChatHub.Infrastructure.RealTime;
public class UserConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _connections = new();
    public void Add(Guid userId, string connectionId)
    {
        var set = _connections.GetOrAdd(userId, _ => []);
        lock (set) set.Add(connectionId);
    }

    public void Remove(Guid userId, string connectionId)
    {
        if (!_connections.TryGetValue(userId, out var set)) return;
        lock (set)
        {
            set.Remove(connectionId);
            if (set.Count == 0) _connections.TryRemove(userId, out _);
        }
    }

    public IReadOnlyList<string> GetConnections(Guid userId)
    {
        if (!_connections.TryGetValue(userId, out var set)) return [];
        lock (set) return set.ToList();
    }
}
