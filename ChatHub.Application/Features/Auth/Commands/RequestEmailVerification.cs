using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Guests;
using ChatHub.Application.Features.Users;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record RequestEmailVerificationCommand : IRequest<Result<EmailVerificationResponse>>;

public sealed record EmailVerificationResponse(string Email, DateTime ExpiresAtUtc);

public sealed class RequestEmailVerificationCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    EmailTokenService emailTokens,
    IOptions<EmailVerificationSettings> options,
    TimeProvider timeProvider) : IRequestHandler<RequestEmailVerificationCommand, Result<EmailVerificationResponse>>
{
    public async Task<Result<EmailVerificationResponse>> Handle(RequestEmailVerificationCommand request,
        CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);
        if (user is null) return UserErrors.NotFound;
        if (user.IsGuest || user.IsDemoPersona) return GuestErrors.AccountRestricted;
        if (user.Email is null) return AuthErrors.NoEmailToVerify;
        if (user.IsEmailVerified) return AuthErrors.EmailAlreadyVerified;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = nowUtc - EmailVerificationSettings.ThrottleWindow;
        var recent = await db.EmailVerificationTokens
            .Where(t => t.UserId == userId && t.CreatedAtUtc > windowStart)
            .CountAsync(cancellationToken);
        if (recent >= options.Value.MaxRequestsPerHour) return AuthErrors.VerificationRequestsThrottled;

        var unused = await db.EmailVerificationTokens
            .Where(t => t.UserId == userId && t.UsedAtUtc == null && t.ExpiresAtUtc > nowUtc)
            .ToListAsync(cancellationToken);
        foreach (var token in unused)
            token.Invalidate(nowUtc);

        var verification = emailTokens.IssueVerification(user);
        await db.SaveChangesAsync(cancellationToken);
        return new EmailVerificationResponse(verification.Email, verification.ExpiresAtUtc);
    }
}
