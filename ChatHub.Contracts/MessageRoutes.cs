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
    public const string UserStatusChanged = "user.status-changed";
    public const string MessageEdited = "message.edited";
    public const string MessageDeleted = "message.deleted";
    public const string MemberLeft = "conversation.member-left";
    public const string MemberKicked = "conversation.member-kicked";
    public const string ConversationMuteChanged = "conversation.mute-changed";

    private static readonly Dictionary<Type, string> RoutingKeys = new()
    {
        [typeof(Events.MessageSent)] = MessageSent,
        [typeof(Events.ConversationRead)] = ConversationRead,
        [typeof(Events.UserRegistered)] = UserRegistered,
        [typeof(Events.UserProfileUpdated)] = UserProfileUpdated,
        [typeof(Events.UserDeleted)] = UserDeleted,
        [typeof(Events.EmailVerificationRequested)] = EmailVerificationRequested,
        [typeof(Events.UserStatusChanged)] = UserStatusChanged,
        [typeof(Events.MessageEdited)] = MessageEdited,
        [typeof(Events.MessageDeleted)] = MessageDeleted,
        [typeof(Events.MemberLeft)] = MemberLeft,
        [typeof(Events.MemberKicked)] = MemberKicked,
        [typeof(Events.ConversationMuteChanged)] = ConversationMuteChanged
    };

    private static readonly Dictionary<string, Type> EventTypes =
        RoutingKeys.ToDictionary(route => route.Value, route => route.Key, StringComparer.Ordinal);

    public static string RoutingKeyOf(IIntegrationEvent integrationEvent) =>
        RoutingKeys.TryGetValue(integrationEvent.GetType(), out var routingKey)
            ? routingKey
            : throw new ArgumentException($"{integrationEvent.GetType().Name} has no routing key.", nameof(integrationEvent));

    public static Type? EventTypeOf(string routingKey) => EventTypes.GetValueOrDefault(routingKey);
}
