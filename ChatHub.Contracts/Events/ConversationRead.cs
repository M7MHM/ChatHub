namespace ChatHub.Contracts.Events;

public sealed record ConversationRead(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid ConversationId,
    Guid UserId,
    DateTime ReadUpToUtc) : IIntegrationEvent;
