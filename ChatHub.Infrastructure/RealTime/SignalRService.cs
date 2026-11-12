using ChatHub.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime;

public class SignalRService(IHubContext<Hubs.ChatHub> hubContext, UserConnectionTracker tracker) : ISignalRService
{
    private static string GroupName(Guid conversationId) => $"conversation-{conversationId}";

    public Task SendToUserAsync(Guid userId, string method, object payload, CancellationToken cancellationToken = default) =>
        hubContext.Clients.User(userId.ToString()).SendAsync(method, payload, cancellationToken);

    public Task SendToConversationAsync(Guid conversationId, string method, object payload, CancellationToken cancellationToken = default) =>
        hubContext.Clients.Group(GroupName(conversationId)).SendAsync(method, payload, cancellationToken);

    public async Task AddToConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in tracker.GetConnections(userId))
            await hubContext.Groups.AddToGroupAsync(connectionId, GroupName(conversationId), cancellationToken);
    }

    public async Task RemoveFromConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in tracker.GetConnections(userId))
            await hubContext.Groups.RemoveFromGroupAsync(connectionId, GroupName(conversationId), cancellationToken);
    }
}
