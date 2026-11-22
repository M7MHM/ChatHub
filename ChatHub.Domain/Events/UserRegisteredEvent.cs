using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record UserRegisteredEvent(Guid UserId) : IDomainEvent;
