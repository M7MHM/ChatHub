namespace ChatHub.Application.Common.Emails;

public sealed class EmailLinks
{
    public const string VerifyEmailRoute = "auth/verify-email";
    public const string UnsubscribeRoute = "email/unsubscribe";
    public const string ResetPasswordPage = "reset-password";

    private readonly string _baseUrl;
    private readonly string _frontendBaseUrl;

    public EmailLinks(string publicApiBaseUrl, string frontendBaseUrl = AppUrlSettings.DefaultFrontendBaseUrl)
    {
        if (!AppUrlSettings.IsValidBaseUrl(publicApiBaseUrl))
            throw new ArgumentException("The public API base URL must be an absolute http(s) URL.", nameof(publicApiBaseUrl));
        if (!AppUrlSettings.IsValidBaseUrl(frontendBaseUrl))
            throw new ArgumentException("The frontend base URL must be an absolute http(s) URL.", nameof(frontendBaseUrl));
        _baseUrl = publicApiBaseUrl.TrimEnd('/');
        _frontendBaseUrl = frontendBaseUrl.TrimEnd('/');
    }

    public string VerifyEmail(string token) => Link(VerifyEmailRoute, token);

    public string Unsubscribe(string token) => Link(UnsubscribeRoute, token);

    public string ResetPassword(string token) => $"{_frontendBaseUrl}/{ResetPasswordPage}?token={Uri.EscapeDataString(token)}";

    private string Link(string route, string token) => $"{_baseUrl}/api/{route}?token={Uri.EscapeDataString(token)}";
}
