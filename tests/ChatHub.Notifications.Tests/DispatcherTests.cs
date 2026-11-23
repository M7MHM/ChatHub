using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Notifications.Tests;

public sealed class DispatcherTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    private static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ChannelFailure_IsRetriedWithExponentialBackoffAndGivenUpAfterMaxAttempts()
    {
        var (bob, chat) = await PendingDigestAsync();
        Channel.FailAlways = true;

        (await DispatchAsync()).ShouldBe(1);
        var digest = (await DigestAsync(bob, chat)).ShouldNotBeNull();
        (digest.Attempts, digest.LastError, digest.LockedUntilUtc).ShouldBe((1, "SMTP relay unavailable", Now + RetryBase));
        (await DispatchAsync()).ShouldBe(0);

        Clock.Advance(RetryBase);
        (await DispatchAsync()).ShouldBe(1);
        (await DigestAsync(bob, chat)).ShouldNotBeNull().LockedUntilUtc.ShouldBe(Now + 2 * RetryBase);

        Clock.Advance(2 * RetryBase);
        (await DispatchAsync()).ShouldBe(1);
        digest = (await DigestAsync(bob, chat)).ShouldNotBeNull();
        digest.Attempts.ShouldBe(3);
        digest.FailedAtUtc.ShouldBe(Now);
        Errors.Take().ShouldHaveSingleItem().ShouldContain("Giving up on the digest");

        Clock.Advance(TimeSpan.FromDays(1));
        (await DispatchAsync()).ShouldBe(0);
        Channel.Sent.ShouldBeEmpty();
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task FailedDigest_GetsANewChanceWhenANewMessageArrives()
    {
        var (bob, chat) = await PendingDigestAsync();
        var alice = await UserAsync("Alice2");
        Channel.FailAlways = true;
        for (var i = 0; i < 3; i++)
        {
            await DispatchAsync();
            Clock.Advance(TimeSpan.FromMinutes(5));
        }

        Errors.Take().ShouldHaveSingleItem();
        Channel.FailAlways = false;

        await ProcessAsync(Message(chat, alice, "Alice2", [bob], "are you there?"));
        var digest = (await DigestAsync(bob, chat)).ShouldNotBeNull();
        (digest.FailedAtUtc, digest.Attempts, digest.MessageCount).ShouldBe((null, 0, 2));
        (await DispatchAsync()).ShouldBe(1);
        Channel.Sent.ShouldHaveSingleItem().MessageCount.ShouldBe(2);
    }

    [Fact]
    public async Task ChannelRecoveringAfterOneFailure_SendsTheDigestOnce()
    {
        var (bob, chat) = await PendingDigestAsync();
        Channel.FailNext(1);

        await DispatchAsync();
        Clock.Advance(RetryBase);
        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.ShouldHaveSingleItem().Recipient.UserId.ShouldBe(bob);
        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task TwoDispatchersRunningConcurrently_NeverSendTheSameDigestTwice()
    {
        var alice = await UserAsync("Alice");
        var recipients = new List<Guid>();
        for (var i = 0; i < 200; i++) recipients.Add(await UserAsync($"User{i}"));
        var group = Guid.CreateVersion7();
        await ProcessAsync(Message(group, alice, "Alice", recipients, type: Contracts.Events.ConversationType.Group, title: "All"));
        Clock.Advance(Delay);

        var first = Services.GetRequiredService<DigestDispatcher>();
        var second = ActivatorUtilities.CreateInstance<DigestDispatcher>(Services);
        var counts = await Task.WhenAll(Task.Run(() => DrainAsync(first)), Task.Run(() => DrainAsync(second)));

        counts.Sum().ShouldBe(200);
        counts.ShouldAllBe(count => count > 0, "both dispatchers should have sent part of the digests");
        Channel.Sent.Select(n => n.Recipient.UserId).ShouldBe(recipients, ignoreOrder: true);
        (await QueryAsync(db => db.SentNotifications.CountAsync())).ShouldBe(200);
        (await DigestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DigestLeasedByAnotherDispatcher_IsSkippedUntilTheLeaseExpires()
    {
        var (bob, chat) = await PendingDigestAsync();
        var leasedUntil = Now + TimeSpan.FromMinutes(2);
        await QueryAsync(db => db.PendingDigests.ExecuteUpdateAsync(s => s.SetProperty(d => d.LockedUntilUtc, leasedUntil)));

        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(TimeSpan.FromMinutes(2));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.ShouldHaveSingleItem().ConversationId.ShouldBe(chat);
        (await DigestAsync(bob, chat)).ShouldBeNull();
    }

    [Fact]
    public async Task MessageArrivingWhileADigestIsBeingSent_IsKeptForTheNextDigest()
    {
        var (bob, chat) = await PendingDigestAsync();
        var alice = await UserAsync("Alice2");
        Channel.OnSend = () => ProcessAsync(Message(chat, alice, "Alice2", [bob], "late arrival")).GetAwaiter().GetResult();

        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.ShouldHaveSingleItem().MessageCount.ShouldBe(1);
        var next = (await DigestAsync(bob, chat)).ShouldNotBeNull();
        (next.MessageCount, next.LastPreview, next.Attempts, next.LockedUntilUtc).ShouldBe((1, "late arrival", 0, null));
        next.DueAtUtc.ShouldBe(Now + Cooldown);
    }

    private async Task<(Guid Bob, Guid Chat)> PendingDigestAsync()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(Delay);
        return (bob, chat);
    }

    private static async Task<int> DrainAsync(DigestDispatcher dispatcher)
    {
        var total = 0;
        int claimed;
        while ((claimed = await dispatcher.DispatchDueAsync(CancellationToken.None)) > 0) total += claimed;
        return total;
    }
}
