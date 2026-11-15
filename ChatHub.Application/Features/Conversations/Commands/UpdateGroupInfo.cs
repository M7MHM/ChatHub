using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Files;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record UpdateGroupInfoCommand(string Title, Guid? ImageFileId = null, bool RemoveImage = false) : IRequest<Result>
{
    public Guid ConversationId { get; init; }
}

public sealed class UpdateGroupInfoCommandValidator : AbstractValidator<UpdateGroupInfoCommand>
{
    public UpdateGroupInfoCommandValidator()
    {
        RuleFor(x => x.ConversationId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Conversation.MaxTitleLength);
        RuleFor(x => x.ImageFileId).NotEqual(Guid.Empty);
        RuleFor(x => x.RemoveImage)
            .Equal(false)
            .When(x => x.ImageFileId is not null)
            .WithMessage("Set a new image or remove it, not both.");
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
        var conversation = loaded.Value;

        var previousImageUrl = conversation.ImageUrl;
        var imageUrl = previousImageUrl;
        if (request.ImageFileId is { } fileId)
        {
            var image = await db.LoadAttachableFileAsync(fileId, me, FilePurpose.GroupImage, cancellationToken);
            if (image.IsFailure) return image.Error;

            image.Value.MarkAttached();
            imageUrl = FileUrls.PathFor(fileId);
        }
        else if (request.RemoveImage)
        {
            imageUrl = null;
        }

        conversation.UpdateGroupInfo(me, request.Title, imageUrl);
        if (imageUrl != previousImageUrl)
            await db.MarkFileDeletedAsync(previousImageUrl, FilePurpose.GroupImage, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
