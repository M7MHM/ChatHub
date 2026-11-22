using ChatHub.Contracts.Events;
using ChatHub.Notifications.Persistence;

namespace ChatHub.Notifications.Digests;

public sealed record DigestNotification(
    Recipient Recipient,
    Guid ConversationId,
    ConversationType ConversationType,
    string? ConversationTitle,
    int MessageCount,
    string LastSenderName,
    string? LastPreview,
    DateTime FirstUnreadAtUtc,
    DateTime LastMessageAtUtc,
    string Link);
