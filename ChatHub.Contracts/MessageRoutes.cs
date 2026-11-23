using ChatHub.Contracts.Events;

namespace ChatHub.Contracts;

public static class MessageRoutes
{
    public const string Exchange = "chathub.events";

    public const string MessageSent = "message.sent";
    public const string ConversationRead = "conversation.read";
    public const string UserRegistered = "user.registered";
    public const string UserProfileUpdated = "user.profile-updated";
    public const string UserDeleted = "user.deleted";
    public const string EmailVerificationRequested = "user.email-verification-requested";

    private static readonly Dictionary<Type, string> RoutingKeys = new()
    {
        [typeof(Events.MessageSent)] = MessageSent,
        [typeof(Events.ConversationRead)] = ConversationRead,
        [typeof(Events.UserRegistered)] = UserRegistered,
        [typeof(Events.UserProfileUpdated)] = UserProfileUpdated,
        [typeof(Events.UserDeleted)] = UserDeleted,
        [typeof(Events.EmailVerificationRequested)] = EmailVerificationRequested
    };

    private static readonly Dictionary<string, Type> EventTypes =
        RoutingKeys.ToDictionary(route => route.Value, route => route.Key, StringComparer.Ordinal);

    public static string RoutingKeyOf(IIntegrationEvent integrationEvent) =>
        RoutingKeys.TryGetValue(integrationEvent.GetType(), out var routingKey)
            ? routingKey
            : throw new ArgumentException($"{integrationEvent.GetType().Name} has no routing key.", nameof(integrationEvent));

    public static Type? EventTypeOf(string routingKey) => EventTypes.GetValueOrDefault(routingKey);
}
