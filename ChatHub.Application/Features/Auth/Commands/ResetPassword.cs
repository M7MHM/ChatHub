using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record ResetPasswordCommand(string Token, string NewPassword) : IRequest<Result>;

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(VerifyEmailCommandValidator.MaxTokenLength);
        RuleFor(x => x.NewPassword).Password();
    }
}

public sealed class ResetPasswordCommandHandler(
    IApplicationDbContext db,
    IIdentityService identity,
    IPasswordHasher passwordHasher,
    AuthTokenService tokens,
    ISignalRService signalR,
    TimeProvider timeProvider) : IRequestHandler<ResetPasswordCommand, Result>
{
    public async Task<Result> Handle(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var tokenHash = identity.HashToken(request.Token);
        var reset = await db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (reset is null) return AuthErrors.InvalidResetToken;

        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == reset.UserId && !u.IsDeleted, cancellationToken);
        if (user?.Email is null || user.Email.Value != reset.Email) return AuthErrors.InvalidResetToken;
        if (reset.IsUsed) return AuthErrors.ResetTokenUsed;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (reset.IsExpired(nowUtc)) return AuthErrors.ResetTokenExpired;

        user.ResetPassword(reset, passwordHasher.Hash(request.NewPassword), nowUtc);

        var otherTokens = await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.Id != reset.Id && t.UsedAtUtc == null && t.ExpiresAtUtc > nowUtc)
            .ToListAsync(cancellationToken);
        foreach (var token in otherTokens)
            token.Invalidate(nowUtc);
        await tokens.RevokeAllAsync(user.Id, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AuthErrors.ResetTokenUsed;
        }

        await signalR.DisconnectUserAsync(user.Id, cancellationToken);
        return Result.Success();
    }
}
