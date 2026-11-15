using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using ChatHub.Application.Features.Users;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword) : IRequest<Result<AuthResponse>>;

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(ValidationExtensions.MaxPasswordLength);
        RuleFor(x => x.NewPassword).Password()
            .NotEqual(x => x.CurrentPassword).WithMessage("The new password must be different from the current one.");
    }
}

public sealed class ChangePasswordCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IPasswordHasher passwordHasher,
    AuthTokenService tokens,
    ISignalRService signalR) : IRequestHandler<ChangePasswordCommand, Result<AuthResponse>>
{
    public async Task<Result<AuthResponse>> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null) return UserErrors.NotFound;

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return AuthErrors.InvalidCurrentPassword;

        user.ChangePassword(passwordHasher.Hash(request.NewPassword));
        await tokens.RevokeAllAsync(user.Id, cancellationToken);

        var response = tokens.Issue(user);
        await db.SaveChangesAsync(cancellationToken);

        await signalR.DisconnectUserAsync(user.Id, cancellationToken);
        return response;
    }
}
