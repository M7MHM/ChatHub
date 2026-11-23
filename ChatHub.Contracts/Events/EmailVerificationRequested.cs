namespace ChatHub.Contracts.Events;

public sealed record EmailVerificationRequested(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid UserId,
    string Email,
    string DisplayName,
    string VerificationLink,
    DateTime ExpiresAtUtc) : IIntegrationEvent;
