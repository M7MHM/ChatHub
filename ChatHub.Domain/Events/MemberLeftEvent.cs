using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MemberLeftEvent(Guid ConversationId, Guid UserId) : IDomainEvent;
