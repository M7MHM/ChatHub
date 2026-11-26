namespace ChatHub.Contracts.Events;

public sealed record PasswordResetCompleted(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid UserId,
    string Email,
    string DisplayName,
    DateTime ChangedAtUtc) : IIntegrationEvent;
