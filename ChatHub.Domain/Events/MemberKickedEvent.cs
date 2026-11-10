using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MemberKickedEvent(Guid ConversationId, Guid KickedUserId, Guid KickedByUserId) : IDomainEvent;
