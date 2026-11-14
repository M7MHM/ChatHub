using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record GroupInfoUpdatedEvent(Guid ConversationId, Guid ActorId) : IDomainEvent;
