using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MessageDeliveredEvent(Guid ConversationId, Guid UserId, Guid MessageId) : IDomainEvent;
