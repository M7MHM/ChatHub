using ChatHub.Application.Features.Users;
using ChatHub.Domain.Enums;

namespace ChatHub.Application.Features.Conversations;

public sealed record ConversationListItem(
    Guid Id,
    ConversationType Type,
    string? Title,
    string? ImageUrl,
    UserSummary? OtherUser,
    LastMessagePreview? LastMessage,
    DateTime? LastMessageAtUtc,
    int UnreadCount,
    bool IsMuted);

public sealed record LastMessagePreview(
    Guid MessageId,
    Guid SenderId,
    string SenderName,
    MessageType Type,
    string Text,
    bool IsDeleted,
    DateTime SentAtUtc);
