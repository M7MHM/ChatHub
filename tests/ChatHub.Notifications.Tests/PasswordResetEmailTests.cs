using System.Text.Json;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Messaging;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Tests.Infrastructure;
using ChatHub.Notifications.Transactional;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Tests;

public sealed class PasswordResetEmailTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Notifications:MaxEmailsPerMinute"] = "1",
        ["Notifications:MaxDigestsPerMinute"] = "1",
        ["Notifications:SuppressedEmailDomains"] = "suppressed.test"
    };

    [Fact]
    public async Task PasswordResetRequested_IsQueuedAndTheNextDispatchSendsItAsTransactional()
    {
        var bob = await UserAsync("Bob");
        var request = Reset(bob, "bob@example.com");

        (await ProcessAsync(request)).ShouldBeTrue();
        Channel.PasswordResets.ShouldBeEmpty();
        var queued = await QueryAsync(db => db.OutgoingEmails.AsNoTracking().SingleAsync());
        (queued.UserId, queued.Template, queued.ToAddress, queued.Link, queued.ExpiresAtUtc)
            .ShouldBe((bob, OutgoingEmailTemplate.PasswordReset, "bob@example.com", request.ResetLink, request.ExpiresAtUtc));

        (await DispatchAsync()).ShouldBe(1);

        Channel.PasswordResets.ShouldHaveSingleItem()
            .ShouldBe(new PasswordResetNotification(bob, "bob@example.com", "Bob", request.ResetLink, request.ExpiresAtUtc));
        var log = await QueryAsync(db => db.SentNotifications.AsNoTracking().SingleAsync());
        (log.UserId, log.ConversationId, log.Kind).ShouldBe((bob, (Guid?)null, NotificationKind.Transactional));
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task PasswordResetRequested_BeforeAnyUserEventOrWithNotificationsOff_GoesToTheAddressInTheEvent()
    {
        var unknown = Guid.CreateVersion7();
        var muted = await UserAsync("Muted", notifications: false);

        await ProcessAsync(Reset(unknown, "new@example.com", "Newcomer"));
        await DispatchAsync();
        Clock.Advance(TimeSpan.FromMinutes(1));
        await ProcessAsync(Reset(muted, "muted@example.com", "Muted"));
        await DispatchAsync();

        Channel.PasswordResets.Select(n => (n.UserId, n.Email)).ShouldBe([(unknown, "new@example.com"), (muted, "muted@example.com")]);
    }

    [Fact]
    public async Task PasswordResetRequested_DeliveredTwice_IsSentOnce()
    {
        var bob = await UserAsync("Bob");
        var request = Reset(bob, "bob@example.com");

        (await ProcessAsync(request)).ShouldBeTrue();
        (await ProcessAsync(request)).ShouldBeFalse();
        await DispatchAsync();
        Clock.Advance(TimeSpan.FromMinutes(1));
        await DispatchAsync();

        Channel.PasswordResets.ShouldHaveSingleItem();
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task PasswordResetRequested_ForADeletedUserOrWithAnExpiredLink_IsNotQueued()
    {
        var gone = await UserAsync("Gone");
        Clock.Advance(TimeSpan.FromSeconds(1));
        await ProcessAsync(new UserDeleted(Guid.CreateVersion7(), Now, gone));
        var bob = await UserAsync("Bob");

        await ProcessAsync(Reset(gone, "gone@example.com"));
        await ProcessAsync(Reset(bob, "bob@example.com", expiresIn: TimeSpan.Zero));
        await DispatchAsync();

        Channel.PasswordResets.ShouldBeEmpty();
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task QueuedResetEmail_WhoseLinkExpiresBeforeItIsSent_IsDropped()
    {
        var bob = await UserAsync("Bob");
        await ProcessAsync(Reset(bob, "bob@example.com", expiresIn: TimeSpan.FromMinutes(1)));
        Clock.Advance(TimeSpan.FromMinutes(1));

        await DispatchAsync();

        Channel.PasswordResets.ShouldBeEmpty();
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task ResetEmailToASuppressedDomain_IsDroppedWithoutUsingTheQuota()
    {
        var bob = await UserAsync("Bob");
        await ProcessAsync(Reset(bob, "bob@mail.suppressed.test"));
        Clock.Advance(TimeSpan.FromSeconds(1));
        await ProcessAsync(Reset(bob, "bob@example.com"));

        await DispatchAsync();
        Channel.PasswordResets.ShouldBeEmpty();
        await DispatchAsync();

        Channel.PasswordResets.Select(n => n.Email).ShouldBe(["bob@example.com"]);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task ResetEmail_GoesOutBeforeADueDigestWhenOnlyOneEmailFitsInTheMinute()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var carol = await UserAsync("Carol");
        await ProcessAsync(Message(Guid.CreateVersion7(), alice, "Alice", [bob]));
        Clock.Advance(Delay);
        await ProcessAsync(Reset(carol, "carol@example.com", "Carol"));

        await DispatchAsync();

        Channel.PasswordResets.ShouldHaveSingleItem().UserId.ShouldBe(carol);
        Channel.Sent.ShouldBeEmpty();

        Clock.Advance(TimeSpan.FromMinutes(1));
        await DispatchAsync();
        Channel.Sent.ShouldHaveSingleItem().Recipient.UserId.ShouldBe(bob);
    }

    [Fact]
    public async Task PasswordResetCompleted_QueuesAndSendsThePasswordChangedNoticeWithTheForgotPasswordLink()
    {
        var bob = await UserAsync("Bob");

        await ProcessAsync(new PasswordResetCompleted(Guid.CreateVersion7(), Now, bob, "bob@example.com", "Bob", Now));
        var queued = await QueryAsync(db => db.OutgoingEmails.AsNoTracking().SingleAsync());
        await DispatchAsync();

        (queued.Template, queued.ExpiresAtUtc).ShouldBe((OutgoingEmailTemplate.PasswordChanged, Now + TimeSpan.FromHours(24)));
        Channel.PasswordChanges.ShouldHaveSingleItem()
            .ShouldBe(new PasswordChangedNotification(bob, "bob@example.com", "Bob", "https://chat.example.test/forgot-password"));
    }

    [Fact]
    public async Task PasswordResetCompleted_MoreThanADayOld_IsNotQueued()
    {
        var bob = await UserAsync("Bob");
        var changedAt = Now - TimeSpan.FromHours(24);

        await ProcessAsync(new PasswordResetCompleted(Guid.CreateVersion7(), changedAt, bob, "bob@example.com", "Bob", changedAt));

        (await QueryAsync(db => db.OutgoingEmails.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public void PasswordResetEvents_HaveTheirOwnRoutingKeysBoundToTheNotificationQueue()
    {
        MessageRoutes.EventTypeOf("user.password-reset-requested").ShouldBe(typeof(PasswordResetRequested));
        MessageRoutes.EventTypeOf("user.password-reset-completed").ShouldBe(typeof(PasswordResetCompleted));
        NotificationTopology.RoutingKeys.ShouldContain(MessageRoutes.PasswordResetRequested);
        NotificationTopology.RoutingKeys.ShouldContain(MessageRoutes.PasswordResetCompleted);
    }

    [Theory]
    [InlineData("bob@example.com", "/reset-password?token=abc", "resetLink")]
    [InlineData("not-an-address", "https://chat.example.test/reset-password?token=abc", "valid email")]
    public void PasswordResetRequested_WithoutAnAbsoluteLinkOrAValidAddress_IsRejectedAsPoison(string email, string link,
        string complaint)
    {
        var e = new PasswordResetRequested(Guid.CreateVersion7(), DateTime.UtcNow, Guid.CreateVersion7(), email, "Bob", link,
            DateTime.UtcNow.AddMinutes(30));

        var result = IntegrationEventReader.Read(MessageRoutes.PasswordResetRequested, e.MessageId.ToString(),
            JsonSerializer.SerializeToUtf8Bytes(e, MessageJson.Options));

        result.Event.ShouldBeNull();
        result.Error.ShouldNotBeNull().ShouldContain(complaint);
    }

    private PasswordResetRequested Reset(Guid userId, string email, string name = "Bob", TimeSpan? expiresIn = null) =>
        new(Guid.CreateVersion7(), Now, userId, email, name, $"https://chat.example.test/reset-password?token={Guid.NewGuid():N}",
            Now + (expiresIn ?? TimeSpan.FromMinutes(30)));
}
