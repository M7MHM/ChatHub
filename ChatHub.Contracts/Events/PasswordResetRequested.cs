namespace ChatHub.Contracts.Events;

public sealed record PasswordResetRequested(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid UserId,
    string Email,
    string DisplayName,
    string ResetLink,
    DateTime ExpiresAtUtc) : IIntegrationEvent;
