using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ChatHub.Notifications.Channels.Email;

public sealed class EmailChannel(IOptions<SmtpSettings> options) : INotificationChannel
{
    public const string ChannelName = "email";

    public string Name => ChannelName;

    public bool CanDeliverTo(Recipient recipient) => !recipient.IsDeleted && !string.IsNullOrWhiteSpace(recipient.Email);

    public async Task SendAsync(DigestNotification notification, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var email = DigestEmailTemplate.Render(notification);

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(new MailboxAddress(notification.Recipient.DisplayName,
            notification.Recipient.Email ?? throw new InvalidOperationException("The recipient has no email address.")));
        message.Subject = email.Subject;
        message.Headers.Add("X-ChatHub-Conversation", notification.ConversationId.ToString());
        message.Body = new BodyBuilder { TextBody = email.Text, HtmlBody = email.Html }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)settings.Timeout.TotalMilliseconds };
        await client.ConnectAsync(settings.Host, settings.Port, SocketOptions(settings.Security), cancellationToken);
        if (!string.IsNullOrEmpty(settings.User))
            await client.AuthenticateAsync(settings.User, settings.Password ?? string.Empty, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static SecureSocketOptions SocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.Auto => SecureSocketOptions.Auto,
        _ => SecureSocketOptions.None
    };
}
