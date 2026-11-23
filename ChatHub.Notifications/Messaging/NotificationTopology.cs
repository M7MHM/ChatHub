using ChatHub.Contracts;
using RabbitMQ.Client;

namespace ChatHub.Notifications.Messaging;

public static class NotificationTopology
{
    public const string Queue = "chathub.notifications";
    public const string RetryQueue = "chathub.notifications.retry";
    public const string DeadLetterExchange = "chathub.events.dlx";
    public const string DeadLetterQueue = "chathub.notifications.dlq";

    public static readonly IReadOnlyList<string> RoutingKeys =
    [
        MessageRoutes.MessageSent,
        MessageRoutes.ConversationRead,
        MessageRoutes.UserRegistered,
        MessageRoutes.UserProfileUpdated,
        MessageRoutes.UserDeleted,
        MessageRoutes.EmailVerificationRequested,
        MessageRoutes.UserStatusChanged,
        MessageRoutes.MessageEdited,
        MessageRoutes.MessageDeleted,
        MessageRoutes.MemberLeft,
        MessageRoutes.MemberKicked,
        MessageRoutes.ConversationMuteChanged
    ];

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(MessageRoutes.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: DeadLetterTo(DeadLetterQueue), cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: DeadLetterTo(Queue), cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);

        foreach (var routingKey in RoutingKeys)
            await channel.QueueBindAsync(Queue, MessageRoutes.Exchange, routingKey, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, DeadLetterExchange, Queue, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, DeadLetterQueue, cancellationToken: cancellationToken);
    }

    private static Dictionary<string, object?> DeadLetterTo(string routingKey) => new()
    {
        ["x-dead-letter-exchange"] = DeadLetterExchange,
        ["x-dead-letter-routing-key"] = routingKey
    };
}
