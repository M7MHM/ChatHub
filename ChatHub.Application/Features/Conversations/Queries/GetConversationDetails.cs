using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Conversations.Queries;

public sealed record GetConversationDetailsQuery(Guid ConversationId) : IRequest<Result<ConversationDetailsResponse>>;

public sealed class GetConversationDetailsQueryValidator : AbstractValidator<GetConversationDetailsQuery>
{
    public GetConversationDetailsQueryValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
    }
}

public sealed class GetConversationDetailsQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<GetConversationDetailsQuery, Result<ConversationDetailsResponse>>
{
    public async Task<Result<ConversationDetailsResponse>> Handle(
        GetConversationDetailsQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var row = await db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == request.ConversationId && !c.IsDeleted)
            .Select(c => new
            {
                c.Id,
                c.Type,
                c.Title,
                c.ImageUrl,
                c.CreatedAtUtc,
                Me = c.Members
                    .Where(m => m.UserId == me && m.IsActive)
                    .Select(m => new { m.Role, m.IsMuted })
                    .FirstOrDefault(),
                Members = c.Members
                    .Where(m => m.IsActive)
                    .OrderByDescending(m => m.Role).ThenBy(m => m.User.IsDeleted).ThenBy(m => m.User.DisplayName)
                    .Select(m => new ConversationMemberResponse(
                        UserSummary.Create(m.User.Id, m.User.DisplayName, m.User.AvatarUrl, m.User.IsOnline,
                            m.User.LastSeenUtc, m.User.IsDeleted),
                        m.Role,
                        m.JoinedAtUtc))
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null) return ConversationErrors.NotFound;
        if (row.Me is null) return ConversationErrors.NotMember;

        var otherUser = row.Type == ConversationType.Direct
            ? row.Members.FirstOrDefault(m => m.User.Id != me)?.User
            : null;

        return new ConversationDetailsResponse(
            row.Id,
            row.Type,
            otherUser is null ? row.Title : otherUser.DisplayName,
            otherUser is null ? row.ImageUrl : otherUser.AvatarUrl,
            otherUser,
            row.CreatedAtUtc,
            row.Me.Role,
            row.Me.IsMuted,
            row.Members);
    }
}

public sealed record ConversationDetailsResponse(
    Guid Id,
    ConversationType Type,
    string? Title,
    string? ImageUrl,
    UserSummary? OtherUser,
    DateTime CreatedAtUtc,
    GroupRole MyRole,
    bool IsMuted,
    IReadOnlyList<ConversationMemberResponse> Members);

public sealed record ConversationMemberResponse(UserSummary User, GroupRole Role, DateTime JoinedAtUtc);
