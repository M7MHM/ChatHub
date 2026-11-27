using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Guests;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.Features.Users.Commands;

public sealed record ChangeUsernameCommand(string Username) : IRequest<Result<UsernameResponse>>;

public sealed record UsernameResponse(string Username, DateTime? NextChangeAtUtc);

public sealed class ChangeUsernameCommandValidator : AbstractValidator<ChangeUsernameCommand>
{
    public const int MaxInputLength = 100;

    public ChangeUsernameCommandValidator()
    {
        RuleFor(x => x.Username).NotNull().MaximumLength(MaxInputLength);
    }
}

public sealed class ChangeUsernameCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IOptions<UsernameSettings> options,
    TimeProvider timeProvider) : IRequestHandler<ChangeUsernameCommand, Result<UsernameResponse>>
{
    public async Task<Result<UsernameResponse>> Handle(ChangeUsernameCommand request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();
        if (!Username.TryCreate(request.Username, out var username, out _))
            return Username.IsReserved(request.Username) ? UserErrors.UsernameReserved : UserErrors.UsernameInvalid;

        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null) return UserErrors.NotFound;
        if (user.IsGuest || user.IsDemoPersona) return GuestErrors.AccountRestricted;

        var cooldown = options.Value.ChangeCooldown;
        if (user.Username == username) return new UsernameResponse(username.Value, user.NextUsernameChangeAtUtc(cooldown));

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (!user.CanChangeUsername(nowUtc, cooldown))
            return UserErrors.UsernameChangeTooSoon(user.NextUsernameChangeAtUtc(cooldown)!.Value);
        if (await db.IsUsernameTakenAsync(username, userId, cancellationToken)) return UserErrors.UsernameTaken;

        user.ChangeUsername(username, nowUtc, cooldown);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return UserErrors.UsernameTaken;
        }

        return new UsernameResponse(username.Value, user.NextUsernameChangeAtUtc(cooldown));
    }
}
