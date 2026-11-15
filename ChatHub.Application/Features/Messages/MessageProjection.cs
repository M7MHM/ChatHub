using System.Linq.Expressions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages;

internal sealed class MessageView
{
    public Message Message { get; init; } = null!;
    public DateTime VisibleAfterUtc { get; init; }
}

internal static class MessageProjection
{
    public static readonly Expression<Func<MessageView, MessageResponse>> Response = v => new MessageResponse(
        v.Message.Id,
        v.Message.ConversationId,
        UserSummary.Create(v.Message.Sender.Id, v.Message.Sender.DisplayName, v.Message.Sender.AvatarUrl,
            v.Message.Sender.IsOnline, v.Message.Sender.LastSeenUtc, v.Message.Sender.IsDeleted),
        v.Message.Type,
        v.Message.IsDeleted ? null : v.Message.Content,
        v.Message.IsDeleted || v.Message.Attachment == null
            ? null
            : new AttachmentResponse(v.Message.Attachment.FileUrl, v.Message.Attachment.FileName,
                v.Message.Attachment.ContentType, v.Message.Attachment.FileSizeBytes),
        v.Message.ParentMessage == null
            ? null
            : new ReplyPreview(
                v.Message.ParentMessage.Id,
                UserSummary.DisplayNameOf(v.Message.ParentMessage.Sender.DisplayName,
                    v.Message.ParentMessage.Sender.IsDeleted),
                v.Message.ParentMessage.CreatedAtUtc > v.VisibleAfterUtc
                    ? MessagePreview.Text(v.Message.ParentMessage.Type, v.Message.ParentMessage.IsDeleted,
                        v.Message.ParentMessage.Content != null &&
                        v.Message.ParentMessage.Content.Length > MessagePreview.MaxLength
                            ? v.Message.ParentMessage.Content.Substring(0, MessagePreview.MaxLength)
                            : v.Message.ParentMessage.Content)
                    : null,
                v.Message.ParentMessage.Type,
                v.Message.ParentMessage.IsDeleted),
        v.Message.IsEdited,
        v.Message.IsDeleted,
        v.Message.CreatedAtUtc,
        v.Message.EditedAtUtc);

    public static IQueryable<MessageResponse> ToResponses(this IQueryable<Message> messages, DateTime visibleAfterUtc) =>
        messages
            .Select(x => new MessageView { Message = x, VisibleAfterUtc = visibleAfterUtc })
            .Select(Response);

    public static Task<MessageResponse?> LoadForViewerAsync(
        this IApplicationDbContext db, Guid messageId, DateTime visibleAfterUtc, CancellationToken cancellationToken) =>
        db.Messages
            .AsNoTracking()
            .Where(x => x.Id == messageId)
            .ToResponses(visibleAfterUtc)
            .FirstOrDefaultAsync(cancellationToken);
    public static async Task<MessageResponse?> LoadForConversationAsync(
        this IApplicationDbContext db, Guid messageId, Guid conversationId, CancellationToken cancellationToken)
    {
        var visibleAfterUtc = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.IsActive)
            .Select(MemberVisibility.Projection)
            .MaxAsync(v => (DateTime?)v.VisibleAfterUtc, cancellationToken);

        return await db.LoadForViewerAsync(messageId, visibleAfterUtc ?? DateTime.UnixEpoch, cancellationToken);
    }
}
