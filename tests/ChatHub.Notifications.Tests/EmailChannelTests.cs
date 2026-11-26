using System.Net.Http.Json;
using System.Text.Json;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels.Email;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Tests.Infrastructure;
using ChatHub.Notifications.Transactional;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ChatHub.Notifications.Tests;

public sealed class MailpitFixture : IAsyncLifetime
{
    public const int SmtpPort = 1025;
    public const int ApiPort = 8025;

    public IContainer Container { get; } = new ContainerBuilder("axllent/mailpit:v1.31")
        .WithPortBinding(SmtpPort, true)
        .WithPortBinding(ApiPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(ApiPort).ForPath("/livez")))
        .Build();

    public HttpClient Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        Api = new HttpClient { BaseAddress = new Uri($"http://{Container.Hostname}:{Container.GetMappedPublicPort(ApiPort)}") };
    }

    public async Task DisposeAsync()
    {
        Api.Dispose();
        await Container.DisposeAsync();
    }
}

public sealed class EmailChannelTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    [Fact]
    public async Task SendAsync_DeliversAPlainTextAndHtmlDigestThroughSmtp()
    {
        await using var channel = Channel(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        var conversationId = Guid.CreateVersion7();

        await channel.SendAsync(Notification(ConversationType.Direct, null, 2, conversationId, recipientEmail: "bob@example.com"),
            CancellationToken.None);

        var messages = await mailpit.Api.GetFromJsonAsync<JsonElement>("/api/v1/messages");
        var summary = messages.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("To")[0].GetProperty("Address").GetString() == "bob@example.com");
        summary.GetProperty("Subject").GetString().ShouldBe("Alice sent you 2 new messages");
        summary.GetProperty("From").GetProperty("Address").GetString().ShouldBe("notifications@chathub.test");

        var message = await mailpit.Api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{summary.GetProperty("ID").GetString()}");
        message.GetProperty("Text").GetString()!.ShouldContain("Latest: \"see you at 5\"");
        message.GetProperty("Text").GetString()!.ShouldContain($"https://chat.example.test/conversations/{conversationId}");
        message.GetProperty("HTML").GetString()!.ShouldContain("Open the conversation");
    }

    [Fact]
    public async Task SendAsync_VerificationEmail_ArrivesWithTheConfirmLinkAndItsExpiryAndNoUnsubscribeHeader()
    {
        await using var channel = Channel(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        var address = $"verify-{Guid.NewGuid():N}@example.com";
        const string link = "https://api.chat.example.test/api/auth/verify-email?token=abc_DEF-123";

        await channel.SendAsync(new EmailVerificationNotification(Guid.CreateVersion7(), address, "Bob", link,
            new DateTime(2026, 11, 24, 10, 30, 0, DateTimeKind.Utc)), CancellationToken.None);

        var (message, headers) = await MessageToAsync(address);
        message.GetProperty("Subject").GetString().ShouldBe("Confirm your ChatHub email");
        message.GetProperty("Text").GetString()!.ShouldContain(link);
        message.GetProperty("Text").GetString()!.ShouldContain("expires on 2026-11-24 at 10:30 UTC");
        message.GetProperty("HTML").GetString()!.ShouldContain($"href=\"{link}\"");
        Header(headers, "X-ChatHub-Kind").ShouldBe("Transactional");
        Header(headers, "List-Unsubscribe").ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_PasswordResetEmail_ArrivesWithTheFrontendLinkItsExpiryAndTheIgnoreHint()
    {
        await using var channel = Channel(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        var address = $"reset-{Guid.NewGuid():N}@example.com";
        const string link = "https://chat.example.test/reset-password?token=abc_DEF-123";

        await channel.SendAsync(new PasswordResetNotification(Guid.CreateVersion7(), address, "Bob", link,
            new DateTime(2026, 11, 26, 10, 30, 0, DateTimeKind.Utc)), CancellationToken.None);

        var (message, headers) = await MessageToAsync(address);
        message.GetProperty("Subject").GetString().ShouldBe("Reset your ChatHub password");
        var text = message.GetProperty("Text").GetString()!;
        text.ShouldContain(link);
        text.ShouldContain("expires on 2026-11-26 at 10:30 UTC");
        text.ShouldContain("If this wasn't you, ignore this email.");
        message.GetProperty("HTML").GetString()!.ShouldContain($"href=\"{link}\"");
        Header(headers, "X-ChatHub-Kind").ShouldBe("Transactional");
        Header(headers, "List-Unsubscribe").ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_PasswordChangedEmail_ArrivesWithTheForgotPasswordLink()
    {
        await using var channel = Channel(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        var address = $"changed-{Guid.NewGuid():N}@example.com";
        const string link = "https://chat.example.test/forgot-password";

        await channel.SendAsync(new PasswordChangedNotification(Guid.CreateVersion7(), address, "Bob", link), CancellationToken.None);

        var (message, headers) = await MessageToAsync(address);
        message.GetProperty("Subject").GetString().ShouldBe("Your ChatHub password was changed");
        var text = message.GetProperty("Text").GetString()!;
        text.ShouldContain("If this wasn't you, reset your password right away:");
        text.ShouldContain(link);
        text.ShouldContain(address);
        message.GetProperty("HTML").GetString()!.ShouldContain($"href=\"{link}\"");
        Header(headers, "X-ChatHub-Kind").ShouldBe("Transactional");
    }

    [Fact]
    public async Task SendAsync_Digest_CarriesOneClickListUnsubscribeHeadersAndAVisibleLink()
    {
        await using var channel = Channel(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        var address = $"digest-{Guid.NewGuid():N}@example.com";
        const string unsubscribe = "https://api.chat.example.test/api/email/unsubscribe?token=XYZ_123-abc";

        await channel.SendAsync(Notification(ConversationType.Direct, null, 2, Guid.CreateVersion7(), recipientEmail: address,
            unsubscribeLink: unsubscribe), CancellationToken.None);

        var (message, headers) = await MessageToAsync(address);
        Header(headers, "List-Unsubscribe").ShouldBe($"<{unsubscribe}>");
        Header(headers, "List-Unsubscribe-Post").ShouldBe("List-Unsubscribe=One-Click");
        Header(headers, "X-ChatHub-Kind").ShouldBe("Digest");
        message.GetProperty("Text").GetString()!.ShouldContain($"Unsubscribe from these emails: {unsubscribe}");
        message.GetProperty("HTML").GetString()!.ShouldContain($"href=\"{unsubscribe}\"");
    }

    [Fact]
    public async Task SendAsync_ABatchOfDigests_UsesOneAuthenticatedConnection()
    {
        await using var proxy = new CountingTcpProxy(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        await using var channel = Channel("127.0.0.1", proxy.Port);
        var recipients = Enumerable.Range(1, 3).Select(i => $"batch{i}-{Guid.NewGuid():N}@example.com").ToList();

        foreach (var recipient in recipients)
            await channel.SendAsync(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7(), recipientEmail: recipient),
                CancellationToken.None);

        proxy.AcceptedConnections.ShouldBe(1);
        (await DeliveredToAsync(recipients)).ShouldBe(3);
    }

    [Fact]
    public async Task SendAsync_AfterTheConnectionWasDropped_ReconnectsAndDelivers()
    {
        await using var proxy = new CountingTcpProxy(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        var log = new RecordingLogger();
        await using var channel = Channel("127.0.0.1", proxy.Port, logger: log);
        var recipients = Enumerable.Range(1, 2).Select(i => $"drop{i}-{Guid.NewGuid():N}@example.com").ToList();

        await channel.SendAsync(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7(), recipientEmail: recipients[0]),
            CancellationToken.None);
        proxy.DropAll();
        await channel.SendAsync(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7(), recipientEmail: recipients[1]),
            CancellationToken.None);

        proxy.AcceptedConnections.ShouldBe(2);
        log.Messages.ShouldContain("The reused SMTP connection was dropped; reconnecting.");
        (await DeliveredToAsync(recipients)).ShouldBe(2);
    }

    [Fact]
    public async Task SendAsync_AfterTheIdleTimeout_ClosesTheConnectionAndOpensANewOne()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var proxy = new CountingTcpProxy(mailpit.Container.Hostname, mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort));
        await using var channel = Channel("127.0.0.1", proxy.Port, clock);
        var recipients = Enumerable.Range(1, 3).Select(i => $"idle{i}-{Guid.NewGuid():N}@example.com").ToList();

        await channel.SendAsync(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7(), recipientEmail: recipients[0]),
            CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(29));
        await channel.SendAsync(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7(), recipientEmail: recipients[1]),
            CancellationToken.None);
        proxy.AcceptedConnections.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(30));
        await channel.SendAsync(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7(), recipientEmail: recipients[2]),
            CancellationToken.None);

        proxy.AcceptedConnections.ShouldBe(2);
        (await DeliveredToAsync(recipients)).ShouldBe(3);
    }

    [Theory]
    [InlineData(ConversationType.Direct, null, 1, "Alice sent you 1 new message")]
    [InlineData(ConversationType.Direct, null, 3, "Alice sent you 3 new messages")]
    [InlineData(ConversationType.Group, "Weekend trip", 1, "1 new message in Weekend trip")]
    [InlineData(ConversationType.Group, "Weekend trip", 4, "4 new messages in Weekend trip")]
    [InlineData(ConversationType.Group, null, 2, "2 new messages in a group chat")]
    public void Template_SubjectDependsOnTheConversationTypeAndCount(ConversationType type, string? title, int count, string subject)
    {
        DigestEmailTemplate.Render(Notification(type, title, count, Guid.CreateVersion7())).Subject.ShouldBe(subject);
    }

    [Fact]
    public void Template_GroupBodyNamesTheLatestSenderAndEncodesUserContentInHtml()
    {
        var email = DigestEmailTemplate.Render(Notification(ConversationType.Group, "<Team>", 2, Guid.CreateVersion7(),
            preview: "<script>alert(1)</script>"));

        email.Text.ShouldContain("You have 2 new messages in <Team>.");
        email.Text.ShouldContain("Latest from Alice: \"<script>alert(1)</script>\"");
        email.Html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        email.Html.ShouldNotContain("<script>");
        email.Html.ShouldContain("&lt;Team&gt;");
    }

    [Fact]
    public void Template_DigestWithoutAnUnsubscribeLink_PointsToTheSettings()
    {
        var email = DigestEmailTemplate.Render(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7()));

        email.Text.ShouldContain("You can turn these emails off in your ChatHub settings.");
        email.Html.ShouldNotContain("Unsubscribe</a>");
    }

    [Fact]
    public void Template_Verification_EncodesTheNameAddressAndLinkInHtml()
    {
        var email = VerificationEmailTemplate.Render(new EmailVerificationNotification(Guid.CreateVersion7(), "o'neil@example.com",
            "<b>Bob</b>", "https://api.chat.example.test/api/auth/verify-email?token=a&x=\"y\"", DateTime.UtcNow));

        email.Subject.ShouldBe("Confirm your ChatHub email");
        email.Html.ShouldContain("Hi &lt;b&gt;Bob&lt;/b&gt;,");
        email.Html.ShouldContain("o&#39;neil@example.com");
        email.Html.ShouldContain("token=a&amp;x=&quot;y&quot;");
        email.Html.ShouldNotContain("<b>Bob</b>");
    }

    [Fact]
    public void Template_WithoutPreview_LeavesTheMessageTextOut()
    {
        var email = DigestEmailTemplate.Render(Notification(ConversationType.Direct, null, 1, Guid.CreateVersion7(), preview: null));

        email.Text.ShouldNotContain("Latest");
        email.Html.ShouldNotContain("blockquote");
    }

    private static EmailChannel Channel(string host, int port, TimeProvider? timeProvider = null, ILogger<EmailChannel>? logger = null) =>
        new(Options.Create(new SmtpSettings { Host = host, Port = port, FromAddress = "notifications@chathub.test", FromName = "ChatHub" }),
            timeProvider ?? TimeProvider.System, logger ?? NullLogger<EmailChannel>.Instance);

    private async Task<int> DeliveredToAsync(IReadOnlyCollection<string> recipients)
    {
        var messages = await mailpit.Api.GetFromJsonAsync<JsonElement>("/api/v1/messages?limit=500");
        return messages.GetProperty("messages").EnumerateArray()
            .Count(m => recipients.Contains(m.GetProperty("To")[0].GetProperty("Address").GetString()!));
    }

    private async Task<(JsonElement Message, JsonElement Headers)> MessageToAsync(string address)
    {
        var messages = await mailpit.Api.GetFromJsonAsync<JsonElement>("/api/v1/messages?limit=500");
        var id = messages.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("To")[0].GetProperty("Address").GetString() == address)
            .GetProperty("ID").GetString();
        return (await mailpit.Api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}"),
            await mailpit.Api.GetFromJsonAsync<JsonElement>($"/api/v1/message/{id}/headers"));
    }

    private static string? Header(JsonElement headers, string name) =>
        headers.EnumerateObject()
            .Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Value[0].GetString())
            .FirstOrDefault();

    private static DigestNotification Notification(ConversationType type, string? title, int count, Guid conversationId,
        string? preview = "see you at 5", string recipientEmail = "bob@example.com", string? unsubscribeLink = null) =>
        new(new Recipient { UserId = Guid.CreateVersion7(), Email = recipientEmail, DisplayName = "Bob", UnsubscribeLink = unsubscribeLink },
            conversationId, type, title, count, "Alice", preview, DateTime.UtcNow, DateTime.UtcNow,
            $"https://chat.example.test/conversations/{conversationId}");

    private sealed class RecordingLogger : ILogger<EmailChannel>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }
}
