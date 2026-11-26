using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record LastSeenVisibilityChangedEvent(Guid UserId, bool ShowLastSeen) : IDomainEvent;
