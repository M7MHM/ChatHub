using ChatHub.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace ChatHub.Infrastructure.RealTime;

public class SignalRService(
    IHubContext<Hubs.ChatHub> hubContext,
    IUserConnectionTracker tracker,
    ILogger<SignalRService> logger) : ISignalRService
{
    private static readonly TimeSpan GroupActionTimeout = TimeSpan.FromSeconds(5);

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

    public Task AddToConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(userId, conversationId, hubContext.Groups.AddToGroupAsync, cancellationToken);

    public Task RemoveFromConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default) =>
        UpdateGroupAsync(userId, conversationId, hubContext.Groups.RemoveFromGroupAsync, cancellationToken);

    public Task DisconnectUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        tracker.DisconnectAsync(userId, cancellationToken);

    private async Task UpdateGroupAsync(Guid userId, Guid conversationId,
        Func<string, string, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var group = ConversationGroups.Name(conversationId);
        IReadOnlyList<string> connectionIds;
        try
        {
            connectionIds = await tracker.GetConnectionIdsAsync(userId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Could not look up the connections of user {UserId} to update group {Group}.", userId, group);
            return;
        }

        await Task.WhenAll(connectionIds.Select(async connectionId =>
        {
            try
            {
                await action(connectionId, group, cancellationToken).WaitAsync(GroupActionTimeout, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Could not update group {Group} for connection {ConnectionId} of user {UserId}.",
                    group, connectionId, userId);
            }
        }));
    }
}
