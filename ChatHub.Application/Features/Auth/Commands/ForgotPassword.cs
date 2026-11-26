using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record ForgotPasswordCommand(string Email) : IRequest<Result<ForgotPasswordResponse>>;

public sealed record ForgotPasswordResponse(string Message)
{
    public static readonly ForgotPasswordResponse Accepted =
        new("If an account with this verified email address exists, we sent it a link to reset the password.");
}

public sealed class ForgotPasswordCommandValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).ValidEmail();
    }
}

public sealed class ForgotPasswordCommandHandler(
    IApplicationDbContext db,
    EmailTokenService emailTokens,
    IOptions<PasswordResetSettings> options,
    TimeProvider timeProvider,
    ILogger<ForgotPasswordCommandHandler> logger) : IRequestHandler<ForgotPasswordCommand, Result<ForgotPasswordResponse>>
{
    public async Task<Result<ForgotPasswordResponse>> Handle(ForgotPasswordCommand request, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();

        await RequestResetAsync(request.Email, cancellationToken);

        var remaining = options.Value.MinimumResponseTime - timeProvider.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, timeProvider, cancellationToken);
        return ForgotPasswordResponse.Accepted;
    }

    private async Task RequestResetAsync(string address, CancellationToken cancellationToken)
    {
        var email = ContactParser.TryParseEmail(address);
        if (email is null) return;

        var user = await db.UserProfiles.FirstOrDefaultAsync(u => !u.IsDeleted && u.Email == email, cancellationToken);
        if (user is null || !user.IsEmailVerified) return;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = nowUtc - PasswordResetSettings.ThrottleWindow;
        var recent = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.CreatedAtUtc > windowStart)
            .CountAsync(cancellationToken);
        if (recent >= options.Value.MaxRequestsPerHour)
        {
            logger.LogInformation("Password reset for user {UserId} skipped: {Count} requests in the last hour.", user.Id, recent);
            return;
        }

        var unused = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAtUtc == null && t.ExpiresAtUtc > nowUtc)
            .ToListAsync(cancellationToken);
        foreach (var token in unused)
            token.Invalidate(nowUtc);

        emailTokens.IssuePasswordReset(user);
        await db.SaveChangesAsync(cancellationToken);
    }
}
