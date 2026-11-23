namespace ChatHub.Contracts.Events;

public sealed record ConversationMuteChanged(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid ConversationId,
    Guid UserId,
    bool IsMuted) : IIntegrationEvent;
