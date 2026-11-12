namespace ChatHub.Infrastructure.RealTime;

public class UserConnectionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, HashSet<string>> _connections = new();

    public bool Add(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set))
            {
                _connections[userId] = [connectionId];
                return true;
            }

            set.Add(connectionId);
            return false;
        }
    }
    public bool Remove(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var set) || !set.Remove(connectionId))
                return false;

            if (set.Count > 0) return false;
            _connections.Remove(userId);
            return true;
        }
    }

    public IReadOnlyList<string> GetConnections(Guid userId)
    {
        lock (_lock)
            return _connections.TryGetValue(userId, out var set) ? set.ToList() : [];
    }

    public bool IsOnline(Guid userId)
    {
        lock (_lock)
            return _connections.ContainsKey(userId);
    }
}
