using ChatHub.Contracts.Events;
using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Tests.Infrastructure;
using ChatHub.Notifications.Transactional;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Tests;

public sealed class VerificationEmailTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    [Fact]
    public async Task EmailVerificationRequested_IsSentImmediatelyAndLoggedAsTransactional()
    {
        var bob = await UserAsync("Bob", verified: false);
        var request = Request(bob, "bob@example.com");

        (await ProcessAsync(request)).ShouldBeTrue();

        Channel.Verifications.ShouldHaveSingleItem()
            .ShouldBe(new EmailVerificationNotification(bob, "bob@example.com", "Bob", request.VerificationLink, request.ExpiresAtUtc));
        Channel.Sent.ShouldBeEmpty();
        var log = await QueryAsync(db => db.SentNotifications.AsNoTracking().SingleAsync());
        (log.UserId, log.ConversationId, log.Channel, log.Kind, log.MessageCount, log.SentAtUtc)
            .ShouldBe((bob, (Guid?)null, "fake", NotificationKind.Transactional, 0, Now));
        (await DigestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task EmailVerificationRequested_BeforeTheRegistrationEvent_GoesToTheAddressInTheEvent()
    {
        var userId = Guid.CreateVersion7();

        (await ProcessAsync(Request(userId, "new@example.com", "Newcomer"))).ShouldBeTrue();

        var sent = Channel.Verifications.ShouldHaveSingleItem();
        (sent.UserId, sent.Email, sent.DisplayName).ShouldBe((userId, "new@example.com", "Newcomer"));
        (await QueryAsync(db => db.Recipients.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task EmailVerificationRequested_DeliveredTwice_IsSentOnce()
    {
        var bob = await UserAsync("Bob", verified: false);
        var request = Request(bob, "bob@example.com");

        (await ProcessAsync(request)).ShouldBeTrue();
        (await ProcessAsync(request)).ShouldBeFalse();

        Channel.Verifications.ShouldHaveSingleItem();
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task EmailVerificationRequested_WithEmailNotificationsOff_IsStillSent()
    {
        var bob = await UserAsync("Bob", verified: false, notifications: false);

        (await ProcessAsync(Request(bob, "bob@example.com"))).ShouldBeTrue();

        Channel.Verifications.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EmailVerificationRequested_ForADeletedUserOrWithAnExpiredLink_IsNotSent()
    {
        var gone = await UserAsync("Gone", verified: false);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await ProcessAsync(new UserDeleted(Guid.CreateVersion7(), Now, gone));
        var bob = await UserAsync("Bob", verified: false);

        (await ProcessAsync(Request(gone, "gone@example.com"))).ShouldBeTrue();
        (await ProcessAsync(Request(bob, "bob@example.com", expiresIn: TimeSpan.Zero))).ShouldBeTrue();

        Channel.Verifications.ShouldBeEmpty();
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task EmailVerificationRequested_WhenSendingFails_RollsBackSoTheRedeliveryIsSent()
    {
        var bob = await UserAsync("Bob", verified: false);
        var request = Request(bob, "bob@example.com");
        Channel.FailNext(1);

        await Should.ThrowAsync<IOException>(() => ProcessAsync(request));
        (await QueryAsync(db => db.ProcessedMessages.CountAsync(m => m.MessageId == request.MessageId))).ShouldBe(0);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(0);

        (await ProcessAsync(request)).ShouldBeTrue();

        Channel.Verifications.ShouldHaveSingleItem();
    }

    private EmailVerificationRequested Request(Guid userId, string email, string name = "Bob", TimeSpan? expiresIn = null) =>
        new(Guid.CreateVersion7(), Now, userId, email, name,
            $"https://api.chat.example.test/api/auth/verify-email?token={Guid.NewGuid():N}",
            Now + (expiresIn ?? TimeSpan.FromHours(24)));
}
