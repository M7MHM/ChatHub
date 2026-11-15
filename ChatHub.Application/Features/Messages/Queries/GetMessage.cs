using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Files;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages.Queries;

public sealed record GetMessageQuery(Guid MessageId) : IRequest<Result<MessageResponse>>;

public sealed class GetMessageQueryValidator : AbstractValidator<GetMessageQuery>
{
    public GetMessageQueryValidator()
    {
        RuleFor(x => x.MessageId).NotEmpty();
    }
}

public sealed class GetMessageQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser, FileUrls urls)
    : IRequestHandler<GetMessageQuery, Result<MessageResponse>>
{
    public async Task<Result<MessageResponse>> Handle(GetMessageQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();

        var conversationId = await db.Messages
            .AsNoTracking()
            .Where(x => x.Id == request.MessageId)
            .Select(x => (Guid?)x.ConversationId)
            .FirstOrDefaultAsync(cancellationToken);
        if (conversationId is null) return MessageErrors.NotFound;

        var membership = await db.ReadMembershipAsync(conversationId.Value, me, cancellationToken);
        if (membership.IsFailure) return membership.Error;

        var message = await db.Messages
            .AsNoTracking()
            .VisibleTo(membership.Value)
            .Where(x => x.Id == request.MessageId)
            .ToResponses(MemberVisibility.VisibleAfterUtcOf(membership.Value))
            .FirstOrDefaultAsync(cancellationToken);

        return message is null ? MessageErrors.NotFound : message.WithSignedAttachment(urls);
    }
}
