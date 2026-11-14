using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MessageReadEvent(Guid ConversationId, Guid UserId, Guid MessageId, DateTime ReadAtUtc) : IDomainEvent;
