using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record EmailVerificationRequestedEvent(Guid UserId, string Email, string Token, DateTime ExpiresAtUtc) : IDomainEvent;
