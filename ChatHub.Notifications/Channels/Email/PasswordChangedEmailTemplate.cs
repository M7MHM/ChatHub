using System.Net;
using ChatHub.Notifications.Transactional;

namespace ChatHub.Notifications.Channels.Email;

public static class PasswordChangedEmailTemplate
{
    public const string Subject = "Your ChatHub password was changed";

    public static RenderedEmail Render(PasswordChangedNotification n)
    {
        const string signedOut = "Every device that was signed in has been signed out.";
        const string wasYou = "If this was you, there is nothing else to do.";

        var text = string.Join("\n",
            $"Hi {n.DisplayName},",
            "",
            $"The password of the ChatHub account for {n.Email} was just changed with a password reset link. {signedOut}",
            "",
            wasYou,
            "If this wasn't you, reset your password right away:",
            n.ForgotPasswordLink);

        var html = $"""
            <!DOCTYPE html>
            <html>
            <body style="font-family: Arial, Helvetica, sans-serif; color: #1f2933; max-width: 560px; margin: 0 auto; padding: 24px;">
            <p>Hi {Encode(n.DisplayName)},</p>
            <p>The password of the ChatHub account for <strong>{Encode(n.Email)}</strong> was just changed with a password reset link. {Encode(signedOut)}</p>
            <p>{Encode(wasYou)}</p>
            <p>If this wasn't you, <a href="{Encode(n.ForgotPasswordLink)}">reset your password right away</a>.</p>
            </body>
            </html>
            """;

        return new RenderedEmail(Subject, text, html);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
