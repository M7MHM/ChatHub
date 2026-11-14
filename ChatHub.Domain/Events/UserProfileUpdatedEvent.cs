using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record UserProfileUpdatedEvent(Guid UserId) : IDomainEvent;
