namespace ChatHub.Contracts;

public interface IIntegrationEvent
{
    Guid MessageId { get; }
    DateTime OccurredAtUtc { get; }
}
