using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using ChatHub.Domain.Entities;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record UpdateGroupInfoCommand(string Title, string? ImageUrl) : IRequest<Result>
{
    public Guid ConversationId { get; init; }
}

public sealed class UpdateGroupInfoCommandValidator : AbstractValidator<UpdateGroupInfoCommand>
{
    public UpdateGroupInfoCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Conversation.MaxTitleLength);
        RuleFor(x => x.ImageUrl).ValidUrl();
    }
}

public sealed class UpdateGroupInfoCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<UpdateGroupInfoCommand, Result>
{
    public async Task<Result> Handle(UpdateGroupInfoCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var loaded = await db.LoadForMemberAsync(request.ConversationId, me, cancellationToken);
        if (loaded.IsFailure) return loaded.Error;

        loaded.Value.UpdateGroupInfo(me, request.Title, request.ImageUrl);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
