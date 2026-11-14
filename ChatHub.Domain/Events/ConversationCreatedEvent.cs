using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;

namespace ChatHub.Domain.Events;

public sealed record ConversationCreatedEvent(Guid ConversationId, ConversationType Type, IReadOnlyList<Guid> MemberIds) : IDomainEvent;
