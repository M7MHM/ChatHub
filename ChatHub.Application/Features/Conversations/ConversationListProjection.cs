using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Conversations;

internal sealed record ConversationListRow(ConversationListItem Item, DateTime ActivityAtUtc);

internal static class ConversationListProjection
{
    public static IQueryable<ConversationMember> ActiveMembershipsOf(this IApplicationDbContext db, Guid userId) =>
        db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive && !m.Conversation.IsDeleted);

    public static async Task<IReadOnlyList<ConversationListItem>> ForUserAsync(
        this IApplicationDbContext db, Guid userId, IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.ActiveMembershipsOf(userId)
            .Where(m => conversationIds.Contains(m.ConversationId))
            .ToListRowsAsync(db, userId, cancellationToken);
        return rows.Select(r => r.Item).ToList();
    }

    public static async Task<List<ConversationListRow>> ToListRowsAsync(
        this IQueryable<ConversationMember> memberships, IApplicationDbContext db, Guid userId,
        CancellationToken cancellationToken)
    {
        var rows = await memberships
            .Select(MemberVisibility.Projection)
            .Select(r => new
            {
                r.Member.ConversationId,
                r.Member.Conversation.Type,
                r.Member.Conversation.Title,
                r.Member.Conversation.ImageUrl,
                r.Member.Conversation.LastMessageAtUtc,
                ActivityAtUtc = r.Member.Conversation.LastMessageAtUtc ?? r.Member.Conversation.CreatedAtUtc,
                r.Member.IsMuted,
                OtherUser = db.ConversationMembers
                    .Where(o => r.Member.Conversation.Type == ConversationType.Direct &&
                                o.ConversationId == r.Member.ConversationId &&
                                o.UserId != r.Member.UserId)
                    .Select(o => UserSummary.Create(o.User.Id, o.User.Username, o.User.DisplayName, o.User.AvatarUrl,
                        o.User.IsOnline, o.User.LastSeenUtc, o.User.IsDeleted,
                        o.User.ShowLastSeen && r.Member.User.ShowLastSeen))
                    .FirstOrDefault(),
                LastMessage = db.Messages
                    .Where(x => x.Id == r.Member.Conversation.LastMessageId && x.CreatedAtUtc > r.VisibleAfterUtc)
                    .Select(x => new
                    {
                        x.Id,
                        x.SenderId,
                        SenderName = UserSummary.DisplayNameOf(x.Sender.DisplayName, x.Sender.IsDeleted),
                        x.Type,
                        x.IsDeleted,
                        Content = x.Content != null && x.Content.Length > MessagePreview.MaxLength
                            ? x.Content.Substring(0, MessagePreview.MaxLength)
                            : x.Content,
                        x.CreatedAtUtc
                    })
                    .FirstOrDefault(),
                UnreadCount = db.Messages.Count(x =>
                    x.ConversationId == r.Member.ConversationId &&
                    x.SenderId != userId &&
                    !x.IsDeleted &&
                    x.CreatedAtUtc > (r.Member.LastReadMessageAtUtc != null &&
                                      r.Member.LastReadMessageAtUtc > r.VisibleAfterUtc
                        ? r.Member.LastReadMessageAtUtc.Value
                        : r.VisibleAfterUtc))
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r =>
        {
            var isDirect = r.Type == ConversationType.Direct;
            var lastMessage = r.LastMessage is { } x
                ? new LastMessagePreview(x.Id, x.SenderId, x.SenderName, x.Type,
                    MessagePreview.Text(x.Type, x.IsDeleted, x.Content), x.IsDeleted, x.CreatedAtUtc)
                : null;

            var item = new ConversationListItem(
                r.ConversationId,
                r.Type,
                isDirect ? r.OtherUser?.DisplayName : r.Title,
                isDirect ? r.OtherUser?.AvatarUrl : r.ImageUrl,
                r.OtherUser,
                lastMessage,
                r.LastMessageAtUtc,
                r.UnreadCount,
                r.IsMuted);
            return new ConversationListRow(item, r.ActivityAtUtc);
        }).ToList();
    }
}
