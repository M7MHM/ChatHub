using ChatHub.Application.Features.Users;
using ChatHub.Domain.Enums;

namespace ChatHub.Application.Features.Messages;

public sealed record MessageResponse(
    Guid Id,
    Guid ConversationId,
    UserSummary Sender,
    MessageType Type,
    string? Content,
    AttachmentResponse? Attachment,
    ReplyPreview? ReplyTo,
    bool IsEdited,
    bool IsDeleted,
    DateTime CreatedAtUtc,
    DateTime? EditedAtUtc);

public sealed record AttachmentResponse(string Url, string FileName, string ContentType, long SizeBytes);

public sealed record ReplyPreview(Guid MessageId, string SenderName, string? Text, MessageType Type, bool IsDeleted);
