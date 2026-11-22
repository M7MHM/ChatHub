using System.Net.Http.Json;
using System.Text.Json;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels.Email;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Tests.Infrastructure;
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

    private static DigestNotification Notification(ConversationType type, string? title, int count, Guid conversationId,
        string? preview = "see you at 5", string recipientEmail = "bob@example.com") =>
        new(new Recipient { UserId = Guid.CreateVersion7(), Email = recipientEmail, DisplayName = "Bob" },
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
