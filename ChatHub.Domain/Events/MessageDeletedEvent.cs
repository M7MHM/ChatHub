using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MessageDeletedEvent(Guid MessageId, Guid ConversationId) : IDomainEvent;
