using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record EmailNotificationsChangedEvent(Guid UserId, bool Enabled) : IDomainEvent;
