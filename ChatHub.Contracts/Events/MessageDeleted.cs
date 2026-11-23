namespace ChatHub.Contracts.Events;

public sealed record MessageDeleted(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid ChatMessageId,
    Guid ConversationId) : IIntegrationEvent;
