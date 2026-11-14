using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MessageEditedEvent(Guid MessageId, Guid ConversationId) : IDomainEvent;
