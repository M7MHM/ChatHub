using ChatHub.Contracts.Events;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Sending;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace ChatHub.Notifications.Tests;

public sealed class SuppressionTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Notifications:SuppressedEmailDomains"] = NotificationSettings.ReservedEmailDomains
    };

    [Fact]
    public async Task ReservedDomains_GetNoDigestsOrVerificationEmailsAndUseNoQuota()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var carol = await RealUserAsync("Carol", "carol@mail.chathub.dev");
        var group = Guid.CreateVersion7();
        await ProcessAsync(Message(group, alice, "Alice", [bob, carol], type: ConversationType.Group, title: "Team"));
        (await DigestAsync(bob, group)).ShouldBeNull();
        await ProcessAsync(new EmailVerificationRequested(Guid.CreateVersion7(), Now, Guid.CreateVersion7(), "seed-0001@chathub.test",
            "Seed", "https://api.chat.example.test/api/auth/verify-email?token=abc", Now + TimeSpan.FromHours(24)));

        Clock.Advance(Delay);
        await DispatchAsync();

        Channel.Verifications.ShouldBeEmpty();
        Channel.Sent.ShouldHaveSingleItem().Recipient.UserId.ShouldBe(carol);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
        Services.GetRequiredService<SendQuota>().SkippedSuppressed.ShouldBe(1);
    }

    [Fact]
    public async Task DigestForAnAddressThatBecameSuppressed_IsSkippedAtSendTime()
    {
        var alice = await RealUserAsync("Alice", "alice@mail.chathub.dev");
        var carol = await RealUserAsync("Carol", "carol@mail.chathub.dev");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [carol]));
        Clock.Advance(TimeSpan.FromSeconds(10));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, carol, "carol@shop.example", "Carol", EmailVerified: true));

        Clock.Advance(Delay);
        await DispatchAsync();

        Channel.Sent.ShouldBeEmpty();
        (await DigestAsync(carol, chat)).ShouldBeNull();
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(0);
        Services.GetRequiredService<SendQuota>().SkippedSuppressed.ShouldBe(1);
    }

    [Theory]
    [InlineData("seed-0001@chathub.test", true)]
    [InlineData("BOB@Mail.Example.COM", true)]
    [InlineData("bob@example.com.", true)]
    [InlineData("bob@localhost", true)]
    [InlineData("bob@sub.invalid", true)]
    [InlineData("bob@shop.example", true)]
    [InlineData("bob@example.co", false)]
    [InlineData("bob@notexample.com", false)]
    [InlineData("bob@testing.dev", false)]
    public void EmailSuppression_MatchesReservedDomainsAndTheirSubdomainsOnly(string address, bool suppressed)
    {
        new EmailSuppression(Options.Create(new NotificationSettings())).IsSuppressed(address).ShouldBe(suppressed);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    public void WorkerDefaults_SuppressReservedDomainsEverywhereButDevelopment(string environment, bool suppressed)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(WorkerProjectDirectory())
            .AddJsonFile("appsettings.json")
            .Build();
        var settings = configuration.GetSection(NotificationSettings.SectionName).Get<NotificationSettings>()!;

        new EmailSuppression(Options.Create(settings), new HostingEnvironment { EnvironmentName = environment })
            .IsSuppressed("seed-0001@chathub.test").ShouldBe(suppressed);
    }

    [Theory]
    [InlineData("Development", "chathub.test", true)]
    [InlineData("Production", "", false)]
    public void ConfiguredDomains_WinOverTheEnvironmentDefault(string environment, string domains, bool suppressed)
    {
        var settings = new NotificationSettings { SuppressedEmailDomains = domains };

        new EmailSuppression(Options.Create(settings), new HostingEnvironment { EnvironmentName = environment })
            .IsSuppressed("seed-0001@chathub.test").ShouldBe(suppressed);
    }

    private async Task<Guid> RealUserAsync(string name, string email)
    {
        var id = Guid.CreateVersion7();
        (await ProcessAsync(new UserRegistered(Guid.CreateVersion7(), Now, id, email, name, EmailVerified: true))).ShouldBeTrue();
        return id;
    }

    private static string WorkerProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "ChatHub.Notifications");
            if (File.Exists(Path.Combine(candidate, "appsettings.json"))) return candidate;
        }

        throw new DirectoryNotFoundException("ChatHub.Notifications/appsettings.json was not found above the test output.");
    }
}
