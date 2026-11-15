using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime;

public class UserConnectionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, HubCallerContext>> _connections = new();

    public bool Add(Guid userId, HubCallerContext connection)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var userConnections))
            {
                _connections[userId] = new Dictionary<string, HubCallerContext> { [connection.ConnectionId] = connection };
                return true;
            }

            userConnections[connection.ConnectionId] = connection;
            return false;
        }
    }

    public bool Remove(Guid userId, string connectionId)
    {
        lock (_lock)
        {
            if (!_connections.TryGetValue(userId, out var userConnections) || !userConnections.Remove(connectionId))
                return false;

            if (userConnections.Count > 0) return false;
            _connections.Remove(userId);
            return true;
        }
    }

    public IReadOnlyList<string> GetConnections(Guid userId)
    {
        lock (_lock)
            return _connections.TryGetValue(userId, out var userConnections) ? userConnections.Keys.ToList() : [];
    }

    public IReadOnlyList<HubCallerContext> GetConnectionContexts(Guid userId)
    {
        lock (_lock)
            return _connections.TryGetValue(userId, out var userConnections) ? userConnections.Values.ToList() : [];
    }

    public bool IsOnline(Guid userId)
    {
        lock (_lock)
            return _connections.ContainsKey(userId);
    }
}
