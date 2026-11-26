using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record PasswordResetCompletedEvent(Guid UserId, string Email, DateTime ChangedAtUtc) : IDomainEvent;
