namespace ChatHub.Application.Common.Interfaces;
public interface ISignalRService
{
    Task SendToUserAsync(Guid userId, string method, object payload, CancellationToken cancellationToken = default);

    Task SendToUsersAsync(IEnumerable<Guid> userIds, string method, object payload, CancellationToken cancellationToken = default);

    Task SendToConversationAsync(Guid conversationId, string method, object payload, CancellationToken cancellationToken = default);

    Task AddToConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default);

    Task RemoveFromConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default);

    Task DisconnectUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
