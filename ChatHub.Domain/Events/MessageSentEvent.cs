using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MessageSentEvent(Guid MessageId, Guid ConversationId, Guid SenderId, DateTime SentAtUtc) : IDomainEvent;
