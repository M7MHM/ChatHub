using ChatHub.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime;

public class SignalRService(IHubContext<Hubs.ChatHub> hubContext, UserConnectionTracker tracker) : ISignalRService
{
    public Task SendToUserAsync(Guid userId, string method, object payload, CancellationToken cancellationToken = default) =>
        hubContext.Clients.User(userId.ToString()).SendAsync(method, payload, cancellationToken);

    public Task SendToUsersAsync(IEnumerable<Guid> userIds, string method, object payload, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().Select(id => id.ToString()).ToList();
        return ids.Count == 0
            ? Task.CompletedTask
            : hubContext.Clients.Users(ids).SendAsync(method, payload, cancellationToken);
    }

    public Task SendToConversationAsync(Guid conversationId, string method, object payload, CancellationToken cancellationToken = default) =>
        hubContext.Clients.Group(ConversationGroups.Name(conversationId)).SendAsync(method, payload, cancellationToken);

    public async Task AddToConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in tracker.GetConnections(userId))
            await hubContext.Groups.AddToGroupAsync(connectionId, ConversationGroups.Name(conversationId), cancellationToken);
    }

    public async Task RemoveFromConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in tracker.GetConnections(userId))
            await hubContext.Groups.RemoveFromGroupAsync(connectionId, ConversationGroups.Name(conversationId), cancellationToken);
    }
}
