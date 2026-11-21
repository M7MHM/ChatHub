using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime;

public sealed class InMemoryUserConnectionTracker : IUserConnectionTracker
{
    private readonly Lock _lock = new();
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

    public IReadOnlyList<(Guid UserId, HubCallerContext Connection)> GetAll()
    {
        lock (_lock)
            return _connections.SelectMany(user => user.Value.Values.Select(connection => (user.Key, connection))).ToList();
    }

    public bool IsOnline(Guid userId)
    {
        lock (_lock)
            return _connections.ContainsKey(userId);
    }

    public void Abort(Guid userId)
    {
        foreach (var connection in GetConnectionContexts(userId))
            connection.Abort();
    }

    public Task<bool> AddAsync(Guid userId, HubCallerContext connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(Add(userId, connection));

    public Task<bool> RemoveAsync(Guid userId, string connectionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Remove(userId, connectionId));

    public Task<bool> IsOnlineAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(IsOnline(userId));

    public Task<IReadOnlyList<string>> GetConnectionIdsAsync(Guid userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetConnections(userId));

    public Task DisconnectAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        Abort(userId);
        return Task.CompletedTask;
    }
}
