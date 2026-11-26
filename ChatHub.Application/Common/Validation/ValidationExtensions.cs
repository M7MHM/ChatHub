using ChatHub.Domain.ValueObjects;
using FluentValidation;

namespace ChatHub.Application.Common.Validation;

public static class ValidationExtensions
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(MinPasswordLength)
            .MaximumLength(MaxPasswordLength)
            .Must(p => p is not null && p.Any(char.IsLetter))
            .WithMessage("'{PropertyName}' must contain at least one letter.")
            .Must(p => p is not null && p.Any(char.IsDigit))
            .WithMessage("'{PropertyName}' must contain at least one digit.");

    public static IRuleBuilderOptions<T, string?> ValidEmail<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => string.IsNullOrWhiteSpace(v) || ContactParser.TryParseEmail(v) is not null)
            .WithMessage("'{PropertyName}' is not a valid email address.");

    public static IRuleBuilderOptions<T, string?> ValidPhoneNumber<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(v => string.IsNullOrWhiteSpace(v) || ContactParser.TryParsePhoneNumber(v) is not null)
            .WithMessage(PhoneNumber.InvalidMessage);

    public static IRuleBuilderOptions<T, string> ValidUsername<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .Must(v => string.IsNullOrWhiteSpace(v) || Username.IsWellFormed(Username.Normalize(v)))
            .WithMessage(Username.RulesMessage)
            .Must(v => !Username.IsReserved(v))
            .WithMessage(Username.ReservedMessage);
}
