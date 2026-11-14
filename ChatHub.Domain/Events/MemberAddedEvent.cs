using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record MemberAddedEvent(Guid ConversationId, Guid UserId, Guid AddedById) : IDomainEvent;
