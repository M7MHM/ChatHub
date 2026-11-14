using ChatHub.Domain.Common;
using ChatHub.Domain.Enums;

namespace ChatHub.Domain.Events;

public sealed record MemberRoleChangedEvent(Guid ConversationId, Guid UserId, GroupRole NewRole) : IDomainEvent;
