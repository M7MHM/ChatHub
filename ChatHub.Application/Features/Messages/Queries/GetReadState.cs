using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages.Queries;

public sealed record GetReadStateQuery(Guid ConversationId) : IRequest<Result<IReadOnlyList<MemberReadState>>>;

public sealed class GetReadStateQueryValidator : AbstractValidator<GetReadStateQuery>
{
    public GetReadStateQueryValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
    }
}

public sealed class GetReadStateQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetReadStateQuery, Result<IReadOnlyList<MemberReadState>>>
{
    public async Task<Result<IReadOnlyList<MemberReadState>>> Handle(
        GetReadStateQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var states = await db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == request.ConversationId && m.IsActive && !m.Conversation.IsDeleted)
            .OrderBy(m => m.JoinedAtUtc)
            .Select(m => new MemberReadState(
                m.UserId,
                m.LastReadMessageId,
                m.LastReadMessageAtUtc,
                m.LastDeliveredMessageId,
                m.LastDeliveredMessageAtUtc))
            .ToListAsync(cancellationToken);

        if (states.All(s => s.UserId != me))
            return await db.MembershipErrorAsync(request.ConversationId, cancellationToken);

        return Result.Success<IReadOnlyList<MemberReadState>>(states);
    }
}

public sealed record MemberReadState(
    Guid UserId,
    Guid? LastReadMessageId,
    DateTime? LastReadMessageAtUtc,
    Guid? LastDeliveredMessageId,
    DateTime? LastDeliveredMessageAtUtc);
