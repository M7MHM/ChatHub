namespace ChatHub.Application.Common.Emails;

public sealed class AppUrlSettings
{
    public const string SectionName = "App";
    public const string DefaultPublicApiBaseUrl = "http://localhost:8080";
    public const string DefaultFrontendBaseUrl = "http://localhost:3000";

    public string PublicApiBaseUrl { get; init; } = DefaultPublicApiBaseUrl;
    public string FrontendBaseUrl { get; init; } = DefaultFrontendBaseUrl;

    public static bool IsValidBaseUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}
