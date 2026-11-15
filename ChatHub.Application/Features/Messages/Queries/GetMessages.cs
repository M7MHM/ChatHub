using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages.Queries;

public sealed record GetMessagesQuery(string? Cursor = null, int PageSize = GetMessagesQuery.DefaultPageSize)
    : IRequest<Result<CursorPage<MessageResponse>>>
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 50;

    public Guid ConversationId { get; init; }
}

public sealed class GetMessagesQueryValidator : AbstractValidator<GetMessagesQuery>
{
    public GetMessagesQueryValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.PageSize).InclusiveBetween(1, GetMessagesQuery.MaxPageSize);
        RuleFor(x => x.Cursor)
            .Must(c => string.IsNullOrEmpty(c) || MessageCursor.TryDecode(c, out _, out _))
            .WithMessage("The cursor is invalid.");
    }
}

public sealed class GetMessagesQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetMessagesQuery, Result<CursorPage<MessageResponse>>>
{
    public async Task<Result<CursorPage<MessageResponse>>> Handle(
        GetMessagesQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var hasCursor = MessageCursor.TryDecode(request.Cursor, out var cursorAt, out var cursorId);
        var take = request.PageSize + 1;

        var messages = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.UserId == me && m.IsActive &&
                        !m.Conversation.IsDeleted)
            .Select(MemberVisibility.Projection)
            .Select(v => db.Messages
                .Where(x => x.ConversationId == v.Member.ConversationId && x.CreatedAtUtc > v.VisibleAfterUtc)
                .Where(x => !hasCursor || x.CreatedAtUtc < cursorAt ||
                            (x.CreatedAtUtc == cursorAt && x.Id.CompareTo(cursorId) < 0))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(take)
                .Select(x => new MessageView { Message = x, VisibleAfterUtc = v.VisibleAfterUtc })
                .Select(MessageProjection.Response)
                .ToList())
            .FirstOrDefaultAsync(cancellationToken);

        if (messages is null) return await db.MembershipErrorAsync(request.ConversationId, cancellationToken);

        var hasMore = messages.Count > request.PageSize;
        var page = hasMore ? messages.Take(request.PageSize).ToList() : messages;
        var nextCursor = hasMore ? MessageCursor.Encode(page[^1].CreatedAtUtc, page[^1].Id) : null;

        return new CursorPage<MessageResponse>(page, nextCursor, hasMore);
    }
}
