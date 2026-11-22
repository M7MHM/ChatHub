using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Auth.Commands;

public sealed record RegisterCommand(string DisplayName, string? Email, string? PhoneNumber, string Password)
    : IRequest<Result<AuthResponse>>;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(UserProfile.MaxDisplayNameLength);
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
    AuthTokenService tokens) : IRequestHandler<RegisterCommand, Result<AuthResponse>>
{
    public async Task<Result<AuthResponse>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var email = ContactParser.TryParseEmail(request.Email);
        var phoneNumber = ContactParser.TryParsePhoneNumber(request.PhoneNumber);

        if (await IsContactInUseAsync(email, phoneNumber, cancellationToken)) return AuthErrors.ContactInUse;

        var user = UserProfile.Register(request.DisplayName, passwordHasher.Hash(request.Password), phoneNumber, email);
        db.UserProfiles.Add(user);

        var response = tokens.Issue(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            if (await IsContactInUseAsync(email, phoneNumber, cancellationToken)) return AuthErrors.ContactInUse;
            throw;
        }

        return response;
    }

    private Task<bool> IsContactInUseAsync(Email? email, PhoneNumber? phoneNumber, CancellationToken cancellationToken) =>
        db.UserProfiles.AnyAsync(u => !u.IsDeleted &&
            ((email != null && u.Email == email) || (phoneNumber != null && u.PhoneNumber == phoneNumber)),
            cancellationToken);
}
