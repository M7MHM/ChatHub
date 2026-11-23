using ChatHub.Contracts.Events;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Tests;

public sealed class DigestEligibilityTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task UnverifiedRecipient_DigestWaitsAndIsSentAfterVerification()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob", verified: false);
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(Delay);

        (await DispatchAsync()).ShouldBe(1);
        Channel.Sent.ShouldBeEmpty();
        (await DigestAsync(bob, chat)).ShouldNotBeNull().LockedUntilUtc.ShouldBe(Now + Recheck);
        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(Recheck);
        (await DispatchAsync()).ShouldBe(1);
        Channel.Sent.ShouldBeEmpty();

        Clock.Advance(TimeSpan.FromMinutes(5));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, bob, "bob@example.com", "Bob", EmailVerified: true));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.ShouldHaveSingleItem().MessageCount.ShouldBe(1);
        (await DigestAsync(bob, chat)).ShouldBeNull();
    }

    [Fact]
    public async Task UnverifiedRecipient_VerifyingBeforeTheDueTime_GetsTheDigestOnTime()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob", verified: false);
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(TimeSpan.FromSeconds(30));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, bob, "bob@example.com", "Bob", EmailVerified: true));

        Clock.Advance(Delay);
        await DispatchAsync();

        Channel.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task UnverifiedRecipient_VerifyingAfterTheMaximumMessageAge_GetsNoDigestAboutTheOldMessages()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob", verified: false);
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(Delay);
        await DispatchAsync();

        Clock.Advance(TimeSpan.FromHours(24));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, bob, "bob@example.com", "Bob", EmailVerified: true));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.ShouldBeEmpty();
        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.DigestMessages.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task AddressChangedButNotYetVerified_DigestIsHeld()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(TimeSpan.FromSeconds(30));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, bob, "robert@example.com", "Robert"));

        Clock.Advance(Delay);
        await DispatchAsync();

        Channel.Sent.ShouldBeEmpty();
        (await DigestAsync(bob, chat)).ShouldNotBeNull();
    }

    [Fact]
    public async Task EmailNotificationsOff_PendingDigestIsDroppedAndNewMessagesCreateNone()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(TimeSpan.FromSeconds(30));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, bob, "bob@example.com", "Bob",
            EmailVerified: true, EmailNotificationsEnabled: false));

        Clock.Advance(Delay);
        await DispatchAsync();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));

        Channel.Sent.ShouldBeEmpty();
        (await DigestAsync(bob, chat)).ShouldBeNull();
    }

    [Fact]
    public async Task EmailNotificationsTurnedBackOn_NewMessagesAreDigestedAgain()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob", notifications: false);
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        (await DigestAsync(bob, chat)).ShouldBeNull();

        Clock.Advance(TimeSpan.FromSeconds(1));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, bob, "bob@example.com", "Bob", EmailVerified: true));
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(Delay);
        await DispatchAsync();

        Channel.Sent.ShouldHaveSingleItem().MessageCount.ShouldBe(1);
    }
}
