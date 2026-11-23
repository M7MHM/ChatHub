namespace ChatHub.Contracts.Events;

public sealed record MemberKicked(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid ConversationId,
    Guid UserId,
    Guid KickedByUserId) : IIntegrationEvent;
