using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using ChatHub.Domain.Entities;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Commands;

public sealed record UpdateMyProfileCommand(string DisplayName, string? AvatarUrl, string? Bio) : IRequest<Result>;

public sealed class UpdateMyProfileCommandValidator : AbstractValidator<UpdateMyProfileCommand>
{
    public UpdateMyProfileCommandValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(UserProfile.MaxDisplayNameLength);
        RuleFor(x => x.AvatarUrl).ValidUrl();
        RuleFor(x => x.Bio).MaximumLength(UserProfile.MaxBioLength);
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

        user.UpdateProfile(request.DisplayName, request.AvatarUrl, request.Bio);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
