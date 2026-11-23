namespace ChatHub.Contracts.Events;

public sealed record MemberLeft(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid ConversationId,
    Guid UserId) : IIntegrationEvent;
