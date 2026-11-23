namespace ChatHub.Contracts.Events;

public sealed record UserStatusChanged(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid UserId,
    bool IsOnline) : IIntegrationEvent;
