using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime;

public interface IUserConnectionTracker
{
    Task<bool> AddAsync(Guid userId, HubCallerContext connection, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(Guid userId, string connectionId, CancellationToken cancellationToken = default);

    Task<bool> IsOnlineAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetConnectionIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task DisconnectAsync(Guid userId, CancellationToken cancellationToken = default);
}
