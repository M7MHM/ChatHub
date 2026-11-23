namespace ChatHub.Application.Common.Emails;

public sealed class EmailLinks
{
    public const string VerifyEmailRoute = "auth/verify-email";
    public const string UnsubscribeRoute = "email/unsubscribe";

    private readonly string _baseUrl;

    public EmailLinks(string publicApiBaseUrl)
    {
        if (!AppUrlSettings.IsValidBaseUrl(publicApiBaseUrl))
            throw new ArgumentException("The public API base URL must be an absolute http(s) URL.", nameof(publicApiBaseUrl));
        _baseUrl = publicApiBaseUrl.TrimEnd('/');
    }

    public string VerifyEmail(string token) => Link(VerifyEmailRoute, token);

    public string Unsubscribe(string token) => Link(UnsubscribeRoute, token);

    private string Link(string route, string token) => $"{_baseUrl}/api/{route}?token={Uri.EscapeDataString(token)}";
}
