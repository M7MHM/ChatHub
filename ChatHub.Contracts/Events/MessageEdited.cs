namespace ChatHub.Contracts.Events;

public sealed record MessageEdited(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid ChatMessageId,
    Guid ConversationId,
    string Preview,
    DateTime EditedAtUtc) : IIntegrationEvent;
