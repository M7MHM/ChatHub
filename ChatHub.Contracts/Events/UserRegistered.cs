namespace ChatHub.Contracts.Events;

public sealed record UserRegistered(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid UserId,
    string? Email,
    string DisplayName) : IIntegrationEvent;
