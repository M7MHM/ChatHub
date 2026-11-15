using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;

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
    public async Task<Result<CursorPage<ConversationListItem>>> Handle(
        GetMyConversationsQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var memberships = db.ActiveMembershipsOf(me);

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
            .ToListRowsAsync(db, me, cancellationToken);

        var hasMore = rows.Count > request.PageSize;
        var page = hasMore ? rows.Take(request.PageSize).ToList() : rows;
        var nextCursor = hasMore ? MessageCursor.Encode(page[^1].ActivityAtUtc, page[^1].Item.Id) : null;

        return new CursorPage<ConversationListItem>(page.Select(r => r.Item).ToList(), nextCursor, hasMore);
    }
}
