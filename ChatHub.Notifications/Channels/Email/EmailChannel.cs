using System.Net.Sockets;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Transactional;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ChatHub.Notifications.Channels.Email;

public sealed class EmailChannel : INotificationChannel, IAsyncDisposable
{
    public const string ChannelName = "email";
    public const string KindHeader = "X-ChatHub-Kind";
    public const string ListUnsubscribeHeader = "List-Unsubscribe";
    public const string ListUnsubscribePostHeader = "List-Unsubscribe-Post";
    public const string ListUnsubscribeOneClick = "List-Unsubscribe=One-Click";

    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(5);

    private readonly SmtpSettings settings;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EmailChannel> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ITimer idleTimer;
    private SmtpClient? client;
    private long lastUsedTimestamp;
    private bool disposed;

    public EmailChannel(IOptions<SmtpSettings> options, TimeProvider timeProvider, ILogger<EmailChannel> logger)
    {
        settings = options.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
        idleTimer = timeProvider.CreateTimer(_ => _ = CloseIfIdleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string Name => ChannelName;

    public bool CanDeliverTo(Recipient recipient) => recipient.CanReceiveEmailDigests;

    public bool CanDeliver(Notification notification) => notification switch
    {
        DigestNotification digest => CanDeliverTo(digest.Recipient),
        EmailVerificationNotification verification => !string.IsNullOrWhiteSpace(verification.Email),
        PasswordResetNotification reset => !string.IsNullOrWhiteSpace(reset.Email),
        PasswordChangedNotification changed => !string.IsNullOrWhiteSpace(changed.Email),
        _ => false
    };

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        var message = BuildMessage(notification);

        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var reused = client is { IsConnected: true } && !IsIdle();
            if (!reused) await ConnectAsync(cancellationToken);

            try
            {
                await client!.SendAsync(message, cancellationToken);
            }
            catch (Exception exception) when (reused && IsConnectionFailure(exception) && !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(exception, "The reused SMTP connection was dropped; reconnecting.");
                await ConnectAsync(cancellationToken);
                await client!.SendAsync(message, cancellationToken);
            }

            lastUsedTimestamp = timeProvider.GetTimestamp();
            if (settings.IdleTimeout > TimeSpan.Zero)
                idleTimer.Change(settings.IdleTimeout, Timeout.InfiniteTimeSpan);
            else
                await CloseAsync(quit: true);
        }
        catch
        {
            await CloseAsync(quit: false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await idleTimer.DisposeAsync();
            await CloseAsync(quit: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private MimeMessage BuildMessage(Notification notification)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));

        RenderedEmail email;
        switch (notification)
        {
            case DigestNotification digest:
                email = DigestEmailTemplate.Render(digest);
                message.To.Add(new MailboxAddress(digest.Recipient.DisplayName,
                    digest.Recipient.Email ?? throw new InvalidOperationException("The recipient has no email address.")));
                message.Headers.Add("X-ChatHub-Conversation", digest.ConversationId.ToString());
                if (digest.Recipient.UnsubscribeLink is { } unsubscribe)
                {
                    message.Headers.Add(ListUnsubscribeHeader, $"<{unsubscribe}>");
                    message.Headers.Add(ListUnsubscribePostHeader, ListUnsubscribeOneClick);
                }
                break;
            case EmailVerificationNotification verification:
                email = VerificationEmailTemplate.Render(verification);
                message.To.Add(new MailboxAddress(verification.DisplayName, verification.Email));
                break;
            case PasswordResetNotification reset:
                email = PasswordResetEmailTemplate.Render(reset);
                message.To.Add(new MailboxAddress(reset.DisplayName, reset.Email));
                break;
            case PasswordChangedNotification changed:
                email = PasswordChangedEmailTemplate.Render(changed);
                message.To.Add(new MailboxAddress(changed.DisplayName, changed.Email));
                break;
            default:
                throw new NotSupportedException($"The email channel cannot send {notification.GetType().Name}.");
        }

        message.Headers.Add(KindHeader, notification.Kind.ToString());
        message.Subject = email.Subject;
        message.Body = new BodyBuilder { TextBody = email.Text, HtmlBody = email.Html }.ToMessageBody();
        return message;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await CloseAsync(quit: false);

        var connecting = new SmtpClient { Timeout = (int)settings.Timeout.TotalMilliseconds };
        try
        {
            await connecting.ConnectAsync(settings.Host, settings.Port, SocketOptions(settings.Security), cancellationToken);
            if (!string.IsNullOrEmpty(settings.User))
                await connecting.AuthenticateAsync(settings.User, settings.Password ?? string.Empty, cancellationToken);
        }
        catch
        {
            connecting.Dispose();
            throw;
        }

        client = connecting;
    }

    private async Task CloseIfIdleAsync()
    {
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (!disposed && client is not null && IsIdle()) await CloseAsync(quit: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CloseAsync(bool quit)
    {
        if (client is null) return;
        var closing = client;
        client = null;

        try
        {
            if (quit && closing.IsConnected)
            {
                using var timeout = new CancellationTokenSource(QuitTimeout);
                await closing.DisconnectAsync(true, timeout.Token);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Closing the SMTP connection failed.");
        }
        finally
        {
            closing.Dispose();
        }
    }

    private bool IsIdle() => timeProvider.GetElapsedTime(lastUsedTimestamp) >= settings.IdleTimeout;

    private static bool IsConnectionFailure(Exception exception) =>
        exception is ServiceNotConnectedException or SmtpProtocolException or IOException or SocketException;

    private static SecureSocketOptions SocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        SmtpSecurity.Auto => SecureSocketOptions.Auto,
        _ => SecureSocketOptions.None
    };
}
