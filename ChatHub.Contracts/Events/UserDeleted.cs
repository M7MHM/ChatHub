namespace ChatHub.Contracts.Events;

public sealed record UserDeleted(Guid MessageId, DateTime OccurredAtUtc, Guid UserId) : IIntegrationEvent;
