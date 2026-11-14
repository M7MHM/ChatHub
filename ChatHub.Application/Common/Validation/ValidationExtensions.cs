using FluentValidation;

namespace ChatHub.Application.Common.Validation;

public static class ValidationExtensions
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MaxUrlLength = 2048;

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
            .WithMessage("'{PropertyName}' must be in international format, e.g. +201001234567.");

    public static IRuleBuilderOptions<T, string?> ValidUrl<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(MaxUrlLength)
            .Must(BeValidUrl)
            .WithMessage("'{PropertyName}' must be an http(s) URL or a path starting with '/'.");

    private static bool BeValidUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var url = value.Trim();
        if (url.StartsWith('/'))
            return !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\');

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
