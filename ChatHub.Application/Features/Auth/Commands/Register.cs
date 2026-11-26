using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using ChatHub.Application.Features.Users;
using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record RegisterCommand(string DisplayName, string Username, string? Email, string? PhoneNumber, string Password)
    : IRequest<Result<AuthResponse>>;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(UserProfile.MaxDisplayNameLength);
        RuleFor(x => x.Username).ValidUsername();
        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.Email) || !string.IsNullOrWhiteSpace(x.PhoneNumber))
            .OverridePropertyName("Contact")
            .WithMessage("An email or a phone number is required.");
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.PhoneNumber).ValidPhoneNumber();
        RuleFor(x => x.Password).Password();
    }
}

public sealed class RegisterCommandHandler(
    IApplicationDbContext db,
    IPasswordHasher passwordHasher,
    AuthTokenService tokens,
    EmailTokenService emailTokens) : IRequestHandler<RegisterCommand, Result<AuthResponse>>
{
    public async Task<Result<AuthResponse>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var username = Username.Create(request.Username);
        var email = ContactParser.TryParseEmail(request.Email);
        var phoneNumber = ContactParser.TryParsePhoneNumber(request.PhoneNumber);

        if (await IsContactInUseAsync(email, phoneNumber, cancellationToken)) return AuthErrors.ContactInUse;
        if (await db.IsUsernameTakenAsync(username, null, cancellationToken)) return UserErrors.UsernameTaken;

        var user = UserProfile.Register(username, request.DisplayName, passwordHasher.Hash(request.Password), phoneNumber, email);
        db.UserProfiles.Add(user);
        emailTokens.IssueUnsubscribe(user);
        if (email is not null) emailTokens.IssueVerification(user);

        var response = tokens.Issue(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            if (await IsContactInUseAsync(email, phoneNumber, cancellationToken)) return AuthErrors.ContactInUse;
            if (await db.IsUsernameTakenAsync(username, null, cancellationToken)) return UserErrors.UsernameTaken;
            throw;
        }

        return response;
    }

    private Task<bool> IsContactInUseAsync(Email? email, PhoneNumber? phoneNumber, CancellationToken cancellationToken) =>
        db.UserProfiles.AnyAsync(u => !u.IsDeleted &&
            ((email != null && u.Email == email) || (phoneNumber != null && u.PhoneNumber == phoneNumber)),
            cancellationToken);
}
