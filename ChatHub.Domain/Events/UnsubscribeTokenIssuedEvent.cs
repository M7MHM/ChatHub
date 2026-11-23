using ChatHub.Domain.Common;

namespace ChatHub.Domain.Events;

public sealed record UnsubscribeTokenIssuedEvent(Guid UserId, string Token) : IDomainEvent;
