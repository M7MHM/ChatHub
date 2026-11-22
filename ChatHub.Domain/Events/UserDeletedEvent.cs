using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record UserDeletedEvent(Guid UserId) : IDomainEvent;
