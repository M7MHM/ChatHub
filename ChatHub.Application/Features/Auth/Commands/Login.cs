using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record LoginCommand(string Identifier, string Password) : IRequest<Result<AuthResponse>>;

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Identifier).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(ValidationExtensions.MaxPasswordLength);
    }
}

public sealed class LoginCommandHandler(
    IApplicationDbContext db,
    IPasswordHasher passwordHasher,
    AuthTokenService tokens) : IRequestHandler<LoginCommand, Result<AuthResponse>>
{
    private static string? dummyPasswordHash;
    private static object? dummyPasswordHashLock;

    public async Task<Result<AuthResponse>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await FindActiveUserAsync(request.Identifier, cancellationToken);
        if (user is null)
        {
            _ = passwordHasher.Verify(request.Password, DummyPasswordHash());
            return AuthErrors.InvalidCredentials;
        }

        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
            return AuthErrors.InvalidCredentials;

        var response = tokens.Issue(user);
        await db.SaveChangesAsync(cancellationToken);
        return response;
    }

    private string DummyPasswordHash() =>
        LazyInitializer.EnsureInitialized(ref dummyPasswordHash, ref dummyPasswordHashLock,
            () => passwordHasher.Hash(Guid.NewGuid().ToString("N")));

    private async Task<UserProfile?> FindActiveUserAsync(string identifier, CancellationToken cancellationToken)
    {
        var users = db.UserProfiles.AsNoTracking().Where(u => !u.IsDeleted);

        if (identifier.Contains('@'))
        {
            var email = ContactParser.TryParseEmail(identifier);
            return email is null ? null : await users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        }

        var phoneNumber = ContactParser.TryParsePhoneNumber(identifier);
        if (phoneNumber is not null)
            return await users.FirstOrDefaultAsync(u => u.PhoneNumber == phoneNumber, cancellationToken);

        return Username.TryCreate(identifier, out var username, out _)
            ? await users.FirstOrDefaultAsync(u => u.Username == username, cancellationToken)
            : null;
    }
}
