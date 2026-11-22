using System.Net.Http.Json;
using System.Text.Json;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels.Email;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;

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
        var channel = new EmailChannel(Options.Create(new SmtpSettings
        {
            Host = mailpit.Container.Hostname,
            Port = mailpit.Container.GetMappedPublicPort(MailpitFixture.SmtpPort),
            FromAddress = "notifications@chathub.test",
            FromName = "ChatHub"
        }));
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

    private static DigestNotification Notification(ConversationType type, string? title, int count, Guid conversationId,
        string? preview = "see you at 5", string recipientEmail = "bob@example.com") =>
        new(new Recipient { UserId = Guid.CreateVersion7(), Email = recipientEmail, DisplayName = "Bob" },
            conversationId, type, title, count, "Alice", preview, DateTime.UtcNow, DateTime.UtcNow,
            $"https://chat.example.test/conversations/{conversationId}");
}
