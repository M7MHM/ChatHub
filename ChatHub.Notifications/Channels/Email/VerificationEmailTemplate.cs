using System.Globalization;
using System.Net;
using ChatHub.Notifications.Transactional;

namespace ChatHub.Notifications.Channels.Email;

public static class VerificationEmailTemplate
{
    public const string Subject = "Confirm your ChatHub email";

    public static RenderedEmail Render(EmailVerificationNotification n)
    {
        var expires = n.ExpiresAtUtc.ToString("yyyy-MM-dd 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);
        const string ignore = "If you did not create a ChatHub account with this address, ignore this email.";

        var text = string.Join("\n",
            $"Hi {n.DisplayName},",
            "",
            $"Confirm that {n.Email} is your email address by opening this link:",
            n.VerificationLink,
            "",
            $"The link works once and expires on {expires}.",
            "",
            ignore);

        var html = $"""
            <!DOCTYPE html>
            <html>
            <body style="font-family: Arial, Helvetica, sans-serif; color: #1f2933; max-width: 560px; margin: 0 auto; padding: 24px;">
            <p>Hi {Encode(n.DisplayName)},</p>
            <p>Confirm that <strong>{Encode(n.Email)}</strong> is your email address.</p>
            <p><a href="{Encode(n.VerificationLink)}" style="display: inline-block; padding: 10px 16px; background: #3e7bfa; color: #ffffff; text-decoration: none; border-radius: 6px;">Confirm email</a></p>
            <p style="font-size: 13px; color: #52606d;">Or paste this link into your browser: {Encode(n.VerificationLink)}</p>
            <p style="font-size: 13px; color: #52606d;">The link works once and expires on {Encode(expires)}.</p>
            <p style="font-size: 12px; color: #7b8794;">{Encode(ignore)}</p>
            </body>
            </html>
            """;

        return new RenderedEmail(Subject, text, html);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
