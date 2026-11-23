using ChatHub.Contracts.Events;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Tests;

public sealed class PresenceTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task OnlineRecipient_DigestIsHeldNotDroppedAndBecomesDueDelayAfterGoingOffline()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Status(bob, online: true));
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));

        Clock.Advance(Delay);
        (await DispatchAsync()).ShouldBe(1);
        Channel.Sent.ShouldBeEmpty();
        (await DigestAsync(bob, chat)).ShouldNotBeNull().LockedUntilUtc.ShouldBe(Now + Recheck);
        Clock.Advance(TimeSpan.FromMinutes(5));
        await DispatchAsync();
        Channel.Sent.ShouldBeEmpty();

        await ProcessAsync(Status(bob, online: false));
        var offlineAt = Now;
        (await DigestAsync(bob, chat)).ShouldNotBeNull().DueAtUtc.ShouldBe(offlineAt + Delay);
        Clock.Advance(Delay - TimeSpan.FromSeconds(1));
        await DispatchAsync();
        Channel.Sent.ShouldBeEmpty();

        Clock.Advance(TimeSpan.FromSeconds(1));
        await DispatchAsync();
        Channel.Sent.ShouldHaveSingleItem().MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task GoingOffline_NeverMovesALaterDueTimeForward()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(Delay);
        await DispatchAsync();
        var sentAt = Now;
        Clock.Advance(TimeSpan.FromSeconds(10));
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));

        await ProcessAsync(Status(bob, online: true));
        Clock.Advance(TimeSpan.FromSeconds(5));
        await ProcessAsync(Status(bob, online: false));

        (await DigestAsync(bob, chat)).ShouldNotBeNull().DueAtUtc.ShouldBe(sentAt + Cooldown);
    }

    [Fact]
    public async Task OlderOfflineEventArrivingAfterANewerOnlineOne_IsIgnored()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var offlineAt = Now;
        Clock.Advance(TimeSpan.FromSeconds(10));
        await ProcessAsync(Status(bob, online: true));
        await ProcessAsync(Status(bob, online: false, at: offlineAt));
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));

        Clock.Advance(Delay);
        await DispatchAsync();

        Channel.Sent.ShouldBeEmpty();
        (await QueryAsync(db => db.RecipientPresence.SingleAsync(p => p.UserId == bob))).IsOnline.ShouldBeTrue();
    }

    [Fact]
    public async Task FlappingConnection_OnlyPostponesTheDigestAndSendsItOnce()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));

        for (var i = 0; i < 5; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(20));
            await ProcessAsync(Status(bob, online: true));
            await DispatchAsync();
            Clock.Advance(TimeSpan.FromSeconds(5));
            await ProcessAsync(Status(bob, online: false));
            await DispatchAsync();
        }

        var lastOffline = Now;
        Channel.Sent.ShouldBeEmpty();
        (await DigestAsync(bob, chat)).ShouldNotBeNull().DueAtUtc.ShouldBe(lastOffline + Delay);
        Clock.Advance(Delay);
        await DispatchAsync();
        Clock.Advance(Delay);
        await DispatchAsync();

        Channel.Sent.ShouldHaveSingleItem();
    }

    private UserStatusChanged Status(Guid userId, bool online, DateTime? at = null) =>
        new(Guid.CreateVersion7(), at ?? Now, userId, online);
}
