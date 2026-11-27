using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Guests;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Conversations.Commands;

public sealed record CreateGroupCommand(string Title, Guid? ImageFileId, IReadOnlyList<Guid>? MemberIds)
    : IRequest<Result<CreateGroupResponse>>
{
    public const int MaxInitialMembers = 200;
}

public sealed class CreateGroupCommandValidator : AbstractValidator<CreateGroupCommand>
{
    public CreateGroupCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(Conversation.MaxTitleLength);
        RuleFor(x => x.ImageFileId).NotEqual(Guid.Empty);
        RuleFor(x => x.MemberIds)
            .Must(ids => ids is null || ids.Count <= CreateGroupCommand.MaxInitialMembers)
            .WithMessage($"A group can be created with at most {CreateGroupCommand.MaxInitialMembers} members.");
        RuleForEach(x => x.MemberIds).NotEmpty();
    }
}

public sealed class CreateGroupCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    ISignalRService signalR) : IRequestHandler<CreateGroupCommand, Result<CreateGroupResponse>>
{
    public async Task<Result<CreateGroupResponse>> Handle(CreateGroupCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var requestedIds = (request.MemberIds ?? []).Where(id => id != me).Distinct().ToList();

        var memberIds = requestedIds.Count == 0
            ? []
            : await db.UserProfiles
                .AsNoTracking()
                .Where(u => requestedIds.Contains(u.Id) && !u.IsDeleted)
                .Where(u => !db.UserBlocks.Any(b =>
                    (b.BlockerId == me && b.BlockedId == u.Id) || (b.BlockerId == u.Id && b.BlockedId == me)))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
        if (await db.CheckContactsAsync(me, memberIds, cancellationToken) is { } restricted) return restricted;

        string? imageUrl = null;
        if (request.ImageFileId is { } fileId)
        {
            var image = await db.LoadAttachableFileAsync(fileId, me, FilePurpose.GroupImage, cancellationToken);
            if (image.IsFailure) return image.Error;

            image.Value.MarkAttached();
            imageUrl = FileUrls.PathFor(fileId);
        }

        var conversation = Conversation.CreateGroup(me, request.Title, imageUrl);
        foreach (var userId in memberIds)
            conversation.AddMember(me, userId);

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);

        var allMemberIds = conversation.Members.Select(m => m.UserId).ToList();
        foreach (var userId in allMemberIds)
            await signalR.AddToConversationGroupAsync(userId, conversation.Id, cancellationToken);

        return new CreateGroupResponse(conversation.Id, allMemberIds);
    }
}
public sealed record CreateGroupResponse(Guid ConversationId, IReadOnlyList<Guid> MemberIds);
