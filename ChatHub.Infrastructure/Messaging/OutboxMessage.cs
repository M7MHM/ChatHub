using System.Text.Json;
using ChatHub.Contracts;

namespace ChatHub.Infrastructure.Messaging;

public sealed class OutboxMessage
{
    public const int MaxTypeLength = 100;
    public const int MaxRoutingKeyLength = 100;
    public const int MaxErrorLength = 500;

    private OutboxMessage() { }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string RoutingKey { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LockedUntilUtc { get; private set; }

    public static OutboxMessage From(IIntegrationEvent integrationEvent) => new()
    {
        Id = integrationEvent.MessageId,
        Type = integrationEvent.GetType().Name,
        RoutingKey = MessageRoutes.RoutingKeyOf(integrationEvent),
        Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), MessageJson.Options),
        OccurredAtUtc = integrationEvent.OccurredAtUtc
    };
}
