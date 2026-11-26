using System.Globalization;
using System.Net;
using ChatHub.Notifications.Transactional;

namespace ChatHub.Notifications.Channels.Email;

public static class PasswordResetEmailTemplate
{
    public const string Subject = "Reset your ChatHub password";

    public static RenderedEmail Render(PasswordResetNotification n)
    {
        var expires = n.ExpiresAtUtc.ToString("yyyy-MM-dd 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);
        const string ignore = "If this wasn't you, ignore this email. Your password stays the same.";

        var text = string.Join("\n",
            $"Hi {n.DisplayName},",
            "",
            $"We received a request to reset the password of the ChatHub account for {n.Email}. Choose a new password here:",
            n.ResetLink,
            "",
            $"The link works once and expires on {expires}.",
            "",
            ignore);

        var html = $"""
            <!DOCTYPE html>
            <html>
            <body style="font-family: Arial, Helvetica, sans-serif; color: #1f2933; max-width: 560px; margin: 0 auto; padding: 24px;">
            <p>Hi {Encode(n.DisplayName)},</p>
            <p>We received a request to reset the password of the ChatHub account for <strong>{Encode(n.Email)}</strong>.</p>
            <p><a href="{Encode(n.ResetLink)}" style="display: inline-block; padding: 10px 16px; background: #3e7bfa; color: #ffffff; text-decoration: none; border-radius: 6px;">Choose a new password</a></p>
            <p style="font-size: 13px; color: #52606d;">Or paste this link into your browser: {Encode(n.ResetLink)}</p>
            <p style="font-size: 13px; color: #52606d;">The link works once and expires on {Encode(expires)}.</p>
            <p style="font-size: 12px; color: #7b8794;">{Encode(ignore)}</p>
            </body>
            </html>
            """;

        return new RenderedEmail(Subject, text, html);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
