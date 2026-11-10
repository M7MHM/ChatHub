using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record UserStatusChangedEvent(Guid UserId, bool IsOnline, DateTime LastSeenUtc) : IDomainEvent;
