using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record PasswordResetRequestedEvent(Guid UserId, string Email, string Token, DateTime ExpiresAtUtc) : IDomainEvent;
