namespace ChatHub.Contracts.Events;

public sealed record UserProfileUpdated(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid UserId,
    string? Email,
    string DisplayName) : IIntegrationEvent;
