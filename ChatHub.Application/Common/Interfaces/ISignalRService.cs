namespace ChatHub.Application.Common.Interfaces;

/// <summary>Pushes real-time events to clients. Use the names in <c>ChatEvents</c> as <c>method</c>.</summary>
public interface ISignalRService
{
    Task SendToUserAsync(Guid userId, string method, object payload, CancellationToken cancellationToken = default);

    Task SendToUsersAsync(IEnumerable<Guid> userIds, string method, object payload, CancellationToken cancellationToken = default);

    Task SendToConversationAsync(Guid conversationId, string method, object payload, CancellationToken cancellationToken = default);

    Task AddToConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default);

    Task RemoveFromConversationGroupAsync(Guid userId, Guid conversationId, CancellationToken cancellationToken = default);
}
