using System.Net;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Digests;

namespace ChatHub.Notifications.Channels.Email;

public sealed record RenderedEmail(string Subject, string Text, string Html);

public static class DigestEmailTemplate
{
    private const string GroupFallbackTitle = "a group chat";
    private const string Footer = "You get this email because you have unread messages on ChatHub.";
    private const string NoUnsubscribeLink = "You can turn these emails off in your ChatHub settings.";

    public static RenderedEmail Render(DigestNotification n)
    {
        var messages = n.MessageCount == 1 ? "1 new message" : $"{n.MessageCount} new messages";
        var isGroup = n.ConversationType == ConversationType.Group;
        var title = string.IsNullOrWhiteSpace(n.ConversationTitle) ? GroupFallbackTitle : n.ConversationTitle.Trim();
        var subject = SingleLine(isGroup ? $"{messages} in {title}" : $"{n.LastSenderName} sent you {messages}");
        var summary = isGroup ? $"You have {messages} in {title}." : $"{n.LastSenderName} sent you {messages}.";
        var latest = isGroup ? $"Latest from {n.LastSenderName}" : "Latest";

        List<string> text = [$"Hi {n.Recipient.DisplayName},", "", summary];
        if (n.LastPreview is not null) text.AddRange(["", $"{latest}: \"{n.LastPreview}\""]);
        text.AddRange(["", $"Open the conversation: {n.Link}", "", Footer]);
        var unsubscribe = n.Recipient.UnsubscribeLink;
        text.Add(unsubscribe is null ? NoUnsubscribeLink : $"Unsubscribe from these emails: {unsubscribe}");

        var preview = n.LastPreview is null
            ? ""
            : $"""
               <p style="color: #52606d; margin-bottom: 4px;">{Encode(latest)}:</p>
               <blockquote style="margin: 0 0 16px; padding: 8px 12px; border-left: 3px solid #3e7bfa; background: #f5f7fa;">{Encode(n.LastPreview)}</blockquote>
               """;
        var html = $"""
            <!DOCTYPE html>
            <html>
            <body style="font-family: Arial, Helvetica, sans-serif; color: #1f2933; max-width: 560px; margin: 0 auto; padding: 24px;">
            <p>Hi {Encode(n.Recipient.DisplayName)},</p>
            <p>{Encode(summary)}</p>
            {preview}
            <p><a href="{Encode(n.Link)}" style="display: inline-block; padding: 10px 16px; background: #3e7bfa; color: #ffffff; text-decoration: none; border-radius: 6px;">Open the conversation</a></p>
            <p style="font-size: 12px; color: #7b8794;">{Footer} {UnsubscribeHtml(unsubscribe)}</p>
            </body>
            </html>
            """;

        return new RenderedEmail(subject, string.Join("\n", text), html);
    }

    private static string UnsubscribeHtml(string? link) =>
        link is null
            ? NoUnsubscribeLink
            : $"<a href=\"{Encode(link)}\" style=\"color: #7b8794;\">Unsubscribe</a> from these emails.";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string SingleLine(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
