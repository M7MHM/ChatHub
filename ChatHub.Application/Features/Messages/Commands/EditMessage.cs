using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Files;
using ChatHub.Domain.Entities;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Messages.Commands;

public sealed record EditMessageCommand(string Content) : IRequest<Result<MessageResponse>>
{
    public Guid MessageId { get; init; }
}

public sealed class EditMessageCommandValidator : AbstractValidator<EditMessageCommand>
{
    public EditMessageCommandValidator()
    {
        RuleFor(x => x.MessageId).NotEmpty();
        RuleFor(x => x.Content).NotEmpty().MaximumLength(Message.MaxContentLength);
    }
}

public sealed class EditMessageCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser, FileUrls urls)
    : IRequestHandler<EditMessageCommand, Result<MessageResponse>>
{
    public async Task<Result<MessageResponse>> Handle(EditMessageCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var message = await db.Messages.FirstOrDefaultAsync(x => x.Id == request.MessageId, cancellationToken);
        if (message is null) return MessageErrors.NotFound;

        var membership = await db.LoadMembershipAsync(message.ConversationId, me, cancellationToken);
        if (membership.IsFailure) return membership.Error;

        message.EditContent(me, request.Content);
        await db.SaveChangesAsync(cancellationToken);

        var visibleAfterUtc = MemberVisibility.VisibleAfterUtcOf(membership.Value);
        var response = await db.LoadForViewerAsync(message.Id, visibleAfterUtc, urls, cancellationToken);
        return response!;
    }
}
