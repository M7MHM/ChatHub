using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MemberMuteChangedEvent(Guid ConversationId, Guid UserId, bool IsMuted) : IDomainEvent;
