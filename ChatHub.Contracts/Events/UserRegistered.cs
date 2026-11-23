namespace ChatHub.Contracts.Events;

public sealed record UserRegistered(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid UserId,
    string? Email,
    string DisplayName,
    bool EmailVerified = false,
    bool EmailNotificationsEnabled = true,
    string? UnsubscribeLink = null) : IIntegrationEvent;
