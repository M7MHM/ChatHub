using System.Text.Json;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;

namespace ChatHub.Notifications.Messaging;

public sealed record ReadResult(IIntegrationEvent? Event, string? Error)
{
    public static ReadResult Invalid(string error) => new(null, error);
}

public static class IntegrationEventReader
{
    public static ReadResult Read(string routingKey, string? brokerMessageId, ReadOnlySpan<byte> body)
    {
        var type = MessageRoutes.EventTypeOf(routingKey);
        if (type is null) return ReadResult.Invalid($"Unknown routing key '{routingKey}'.");
        if (!Guid.TryParse(brokerMessageId, out var messageId))
            return ReadResult.Invalid("The message has no valid message_id property.");

        IIntegrationEvent? integrationEvent;
        try
        {
            integrationEvent = JsonSerializer.Deserialize(body, type, MessageJson.Options) as IIntegrationEvent;
        }
        catch (JsonException exception)
        {
            return ReadResult.Invalid($"Malformed {type.Name} payload: {exception.Message}");
        }

        if (integrationEvent is null) return ReadResult.Invalid($"The {type.Name} payload is empty.");
        if (integrationEvent.MessageId != messageId)
            return ReadResult.Invalid($"message_id {messageId} does not match the payload's messageId {integrationEvent.MessageId}.");

        var error = MissingFields(integrationEvent);
        return error is null ? new ReadResult(integrationEvent, null) : ReadResult.Invalid($"Invalid {type.Name}: {error}");
    }

    private static string? MissingFields(IIntegrationEvent integrationEvent) => integrationEvent switch
    {
        MessageSent m when m.ConversationId == Guid.Empty || m.ChatMessageId == Guid.Empty || m.SenderId == Guid.Empty =>
            "conversationId, chatMessageId and senderId are required.",
        MessageSent m when m.RecipientUserIds is null || m.MutedRecipientUserIds is null || m.SenderDisplayName is null =>
            "recipientUserIds, mutedRecipientUserIds and senderDisplayName are required.",
        ConversationRead r when r.UserId == Guid.Empty || r.ConversationId == Guid.Empty =>
            "userId and conversationId are required.",
        UserRegistered u when u.UserId == Guid.Empty || u.DisplayName is null => "userId and displayName are required.",
        UserProfileUpdated u when u.UserId == Guid.Empty || u.DisplayName is null => "userId and displayName are required.",
        UserDeleted d when d.UserId == Guid.Empty => "userId is required.",
        _ when integrationEvent.MessageId == Guid.Empty => "messageId is required.",
        _ => null
    };
}
