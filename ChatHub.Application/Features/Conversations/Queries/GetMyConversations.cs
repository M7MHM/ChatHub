using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Conversations.Queries;

public sealed record GetMyConversationsQuery(string? Cursor = null, int PageSize = GetMyConversationsQuery.DefaultPageSize)
    : IRequest<Result<CursorPage<ConversationListItem>>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
}

public sealed class GetMyConversationsQueryValidator : AbstractValidator<GetMyConversationsQuery>
{
    public GetMyConversationsQueryValidator()
    {
        RuleFor(x => x.PageSize).InclusiveBetween(1, GetMyConversationsQuery.MaxPageSize);
        RuleFor(x => x.Cursor)
            .Must(c => string.IsNullOrEmpty(c) || MessageCursor.TryDecode(c, out _, out _))
            .WithMessage("The cursor is invalid.");
    }
}

public sealed class GetMyConversationsQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetMyConversationsQuery, Result<CursorPage<ConversationListItem>>>
{
    private const int PreviewLength = 100;

    public async Task<Result<CursorPage<ConversationListItem>>> Handle(
        GetMyConversationsQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var memberships = db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == me && m.IsActive && !m.Conversation.IsDeleted);

        if (MessageCursor.TryDecode(request.Cursor, out var cursorAt, out var cursorId))
        {
            memberships = memberships.Where(m =>
                (m.Conversation.LastMessageAtUtc ?? m.Conversation.CreatedAtUtc) < cursorAt ||
                ((m.Conversation.LastMessageAtUtc ?? m.Conversation.CreatedAtUtc) == cursorAt &&
                 m.ConversationId.CompareTo(cursorId) < 0));
        }

        var rows = await memberships
            .OrderByDescending(m => m.Conversation.LastMessageAtUtc ?? m.Conversation.CreatedAtUtc)
            .ThenByDescending(m => m.ConversationId)
            .Take(request.PageSize + 1)
            .Select(m => new
            {
                Member = m,
                VisibleAfterUtc = m.ClearedAtUtc != null && m.ClearedAtUtc > m.JoinedAtUtc
                    ? m.ClearedAtUtc.Value
                    : m.JoinedAtUtc
            })
            .Select(r => new
            {
                r.Member.ConversationId,
                r.Member.Conversation.Type,
                r.Member.Conversation.Title,
                r.Member.Conversation.ImageUrl,
                r.Member.Conversation.LastMessageAtUtc,
                ActivityAtUtc = r.Member.Conversation.LastMessageAtUtc ?? r.Member.Conversation.CreatedAtUtc,
                r.Member.IsMuted,
                OtherUser = r.Member.Conversation.Type == ConversationType.Direct
                    ? r.Member.Conversation.Members
                        .Where(o => o.UserId != me)
                        .Select(o => UserSummary.Create(o.User.Id, o.User.DisplayName, o.User.AvatarUrl,
                            o.User.IsOnline, o.User.LastSeenUtc, o.User.IsDeleted))
                        .FirstOrDefault()
                    : null,
                LastMessage = db.Messages
                    .Where(x => x.Id == r.Member.Conversation.LastMessageId && x.CreatedAtUtc > r.VisibleAfterUtc)
                    .Select(x => new
                    {
                        x.Id,
                        x.SenderId,
                        SenderName = UserSummary.DisplayNameOf(x.Sender.DisplayName, x.Sender.IsDeleted),
                        x.Type,
                        x.IsDeleted,
                        Content = x.Content != null && x.Content.Length > PreviewLength
                            ? x.Content.Substring(0, PreviewLength)
                            : x.Content,
                        x.CreatedAtUtc
                    })
                    .FirstOrDefault(),
                UnreadCount = db.Messages.Count(x =>
                    x.ConversationId == r.Member.ConversationId &&
                    x.SenderId != me &&
                    !x.IsDeleted &&
                    x.CreatedAtUtc > r.VisibleAfterUtc &&
                    (r.Member.LastReadMessageAtUtc == null || x.CreatedAtUtc > r.Member.LastReadMessageAtUtc))
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > request.PageSize;
        var pageRows = hasMore ? rows.Take(request.PageSize).ToList() : rows;

        var items = pageRows.Select(r =>
        {
            var isDirect = r.Type == ConversationType.Direct;
            var lastMessage = r.LastMessage is { } x
                ? new LastMessagePreview(x.Id, x.SenderId, x.SenderName, x.Type,
                    PreviewText(x.Type, x.IsDeleted, x.Content), x.IsDeleted, x.CreatedAtUtc)
                : null;

            return new ConversationListItem(
                r.ConversationId,
                r.Type,
                isDirect ? r.OtherUser?.DisplayName : r.Title,
                isDirect ? r.OtherUser?.AvatarUrl : r.ImageUrl,
                r.OtherUser,
                lastMessage,
                r.LastMessageAtUtc,
                r.UnreadCount,
                r.IsMuted);
        }).ToList();

        var nextCursor = hasMore ? MessageCursor.Encode(pageRows[^1].ActivityAtUtc, pageRows[^1].ConversationId) : null;
        return new CursorPage<ConversationListItem>(items, nextCursor, hasMore);
    }

    private static string PreviewText(MessageType type, bool isDeleted, string? content) =>
        isDeleted
            ? "This message was deleted"
            : type switch
            {
                MessageType.Image => "[Image]",
                MessageType.File => "[File]",
                MessageType.Voice => "[Voice]",
                _ => content ?? string.Empty
            };
}
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
