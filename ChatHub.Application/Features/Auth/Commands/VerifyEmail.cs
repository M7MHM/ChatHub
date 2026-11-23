using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record VerifyEmailCommand(string Token) : IRequest<Result>;

public sealed class VerifyEmailCommandValidator : AbstractValidator<VerifyEmailCommand>
{
    public const int MaxTokenLength = 256;

    public VerifyEmailCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(MaxTokenLength);
    }
}

public sealed class VerifyEmailCommandHandler(
    IApplicationDbContext db,
    IIdentityService identity,
    TimeProvider timeProvider) : IRequestHandler<VerifyEmailCommand, Result>
{
    public async Task<Result> Handle(VerifyEmailCommand request, CancellationToken cancellationToken)
    {
        var tokenHash = identity.HashToken(request.Token);
        var verification = await db.EmailVerificationTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        if (verification is null) return AuthErrors.InvalidVerificationToken;

        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == verification.UserId && !u.IsDeleted, cancellationToken);
        if (user is null) return AuthErrors.InvalidVerificationToken;
        if (user.IsEmailVerified) return AuthErrors.EmailAlreadyVerified;
        if (user.Email is null || user.Email.Value != verification.Email) return AuthErrors.InvalidVerificationToken;
        if (verification.IsUsed) return AuthErrors.VerificationTokenUsed;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (verification.IsExpired(nowUtc)) return AuthErrors.VerificationTokenExpired;

        user.VerifyEmail(verification, nowUtc);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AuthErrors.EmailAlreadyVerified;
        }

        return Result.Success();
    }
}
