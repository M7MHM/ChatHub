using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Files;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;

public sealed record UpdateMyProfileCommand(string DisplayName, string? Bio, Guid? AvatarFileId = null, bool RemoveAvatar = false)
    : IRequest<Result>;

public sealed class UpdateMyProfileCommandValidator : AbstractValidator<UpdateMyProfileCommand>
{
    public UpdateMyProfileCommandValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(UserProfile.MaxDisplayNameLength);
        RuleFor(x => x.Bio).MaximumLength(UserProfile.MaxBioLength);
        RuleFor(x => x.AvatarFileId).NotEqual(Guid.Empty);
        RuleFor(x => x.RemoveAvatar)
            .Equal(false)
            .When(x => x.AvatarFileId is not null)
            .WithMessage("Set a new avatar or remove it, not both.");
    }
}

public sealed class UpdateMyProfileCommandHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<UpdateMyProfileCommand, Result>
{
    public async Task<Result> Handle(UpdateMyProfileCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null) return UserErrors.NotFound;

        var avatarUrl = user.AvatarUrl;
        if (request.AvatarFileId is { } fileId)
        {
            var avatar = await db.LoadAttachableFileAsync(fileId, userId, FilePurpose.Avatar, cancellationToken);
            if (avatar.IsFailure) return avatar.Error;

            avatar.Value.MarkAttached();
            avatarUrl = FileUrls.PathFor(fileId);
        }
        else if (request.RemoveAvatar)
        {
            avatarUrl = null;
        }

        if (avatarUrl != user.AvatarUrl)
            await db.MarkFileDeletedAsync(user.AvatarUrl, FilePurpose.Avatar, cancellationToken);

        user.UpdateProfile(request.DisplayName, avatarUrl, request.Bio);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
