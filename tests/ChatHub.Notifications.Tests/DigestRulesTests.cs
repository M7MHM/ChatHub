using ChatHub.Contracts.Events;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Tests;

public sealed class DigestRulesTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    [Fact]
    public async Task UnreadMessage_IsSentOnceTheDelayHasPassed()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var sentAt = Now;

        await ProcessAsync(Message(chat, alice, "Alice", [bob], "see you at 5"));
        Clock.Advance(Delay - TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(1);

        var sent = Channel.Sent.ShouldHaveSingleItem();
        sent.Recipient.UserId.ShouldBe(bob);
        sent.Recipient.Email.ShouldBe("bob@example.com");
        sent.ConversationType.ShouldBe(ConversationType.Direct);
        sent.MessageCount.ShouldBe(1);
        sent.LastSenderName.ShouldBe("Alice");
        sent.LastPreview.ShouldBe("see you at 5");
        sent.FirstUnreadAtUtc.ShouldBe(sentAt);
        sent.Link.ShouldBe($"https://chat.example.test/conversations/{chat}");
        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.DigestMessages.CountAsync())).ShouldBe(0);
        var log = (await QueryAsync(db => db.SentNotifications.ToListAsync())).ShouldHaveSingleItem();
        (log.UserId, log.ConversationId, log.Channel, log.MessageCount).ShouldBe((bob, chat, "fake", 1));
        (await QueryAsync(db => db.DigestCooldowns.SingleAsync())).LastSentAtUtc.ShouldBe(Now);
        (await DispatchAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task MoreMessagesBeforeTheDigestIsDue_AreFoldedWithoutMovingTheClock()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var first = Now;

        await ProcessAsync(Message(chat, alice, "Alice", [bob], "one"));
        Clock.Advance(TimeSpan.FromSeconds(60));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], "two"));
        Clock.Advance(TimeSpan.FromSeconds(59));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], "three"));

        var digest = (await DigestAsync(bob, chat)).ShouldNotBeNull();
        digest.MessageCount.ShouldBe(3);
        digest.LastPreview.ShouldBe("three");
        digest.FirstUnreadAtUtc.ShouldBe(first);
        digest.DueAtUtc.ShouldBe(first + Delay);

        Clock.Advance(TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(1);
        var sent = Channel.Sent.ShouldHaveSingleItem();
        sent.MessageCount.ShouldBe(3);
        sent.LastPreview.ShouldBe("three");
    }

    [Fact]
    public async Task ReadBeforeTheDigestIsDue_CancelsIt()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var message = Message(chat, alice, "Alice", [bob]);
        await ProcessAsync(message);

        Clock.Advance(TimeSpan.FromSeconds(30));
        await ProcessAsync(Read(bob, chat, message.SentAtUtc));

        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.DigestMessages.CountAsync())).ShouldBe(0);
        Clock.Advance(Delay);
        (await DispatchAsync()).ShouldBe(0);
        Channel.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task PartialRead_RemovesOnlyTheReadMessagesAndRestartsTheClockAtTheFirstUnreadOne()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var one = Message(chat, alice, "Alice", [bob], "one");
        await ProcessAsync(one);
        Clock.Advance(TimeSpan.FromSeconds(10));
        var two = Message(chat, alice, "Alice", [bob], "two");
        await ProcessAsync(two);
        Clock.Advance(TimeSpan.FromSeconds(10));
        var three = Message(chat, alice, "Alice", [bob], "three");
        await ProcessAsync(three);

        await ProcessAsync(Read(bob, chat, two.SentAtUtc));

        var digest = (await DigestAsync(bob, chat)).ShouldNotBeNull();
        digest.MessageCount.ShouldBe(1);
        digest.FirstUnreadAtUtc.ShouldBe(three.SentAtUtc);
        digest.DueAtUtc.ShouldBe(three.SentAtUtc + Delay);
        digest.LastPreview.ShouldBe("three");

        Clock.Advance(Delay - TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(1);
        Channel.Sent.ShouldHaveSingleItem().MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReadArrivingBeforeTheMessageItCovers_PreventsTheDigest()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var message = Message(chat, alice, "Alice", [bob]);

        await ProcessAsync(Read(bob, chat, message.SentAtUtc));
        await ProcessAsync(message);

        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.DigestMessages.CountAsync())).ShouldBe(0);

        Clock.Advance(TimeSpan.FromSeconds(5));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], "newer"));
        (await DigestAsync(bob, chat)).ShouldNotBeNull().MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task OlderReadArrivingLate_NeverMovesTheReadPointerBack()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var first = Now;
        Clock.Advance(TimeSpan.FromSeconds(10));
        var second = Now;

        await ProcessAsync(Read(bob, chat, second));
        await ProcessAsync(Read(bob, chat, first));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], sentAtUtc: second));

        (await QueryAsync(db => db.ConversationReads.SingleAsync())).ReadUpToUtc.ShouldBe(second);
        (await DigestAsync(bob, chat)).ShouldBeNull();
    }

    [Fact]
    public async Task DuplicateDelivery_IsANoOp()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var message = Message(chat, alice, "Alice", [bob]);

        (await ProcessAsync(message)).ShouldBeTrue();
        (await ProcessAsync(message)).ShouldBeFalse();
        (await ProcessAsync(message with { MessageId = Guid.CreateVersion7() })).ShouldBeTrue();

        (await DigestAsync(bob, chat)).ShouldNotBeNull().MessageCount.ShouldBe(1);
        (await QueryAsync(db => db.ProcessedMessages.CountAsync())).ShouldBe(4);
    }

    [Fact]
    public async Task MutedDeletedEmaillessRecipientsAndTheSender_AreSkipped()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var carol = await UserAsync("Carol");
        var dave = await UserAsync("Dave");
        var erin = await UserAsync("Erin", withEmail: false);
        await ProcessAsync(new UserDeleted(Guid.CreateVersion7(), Now, dave));
        var group = Guid.CreateVersion7();

        await ProcessAsync(Message(group, alice, "Alice", [alice, bob, carol, dave, erin], type: ConversationType.Group,
            title: "Team", muted: [carol]));

        (await DigestsAsync()).Select(d => d.UserId).ShouldBe([bob]);
        Clock.Advance(Delay);
        await DispatchAsync();
        Channel.Sent.ShouldHaveSingleItem().Recipient.UserId.ShouldBe(bob);
    }

    [Fact]
    public async Task GroupMessage_FansOutToEveryRecipientWithTheSameNumberOfStatements()
    {
        var alice = await UserAsync("Alice");
        var small = new List<Guid>();
        for (var i = 0; i < 2; i++) small.Add(await UserAsync($"Small{i}"));
        var large = new List<Guid>();
        for (var i = 0; i < 60; i++) large.Add(await UserAsync($"Large{i}"));
        var smallGroup = Guid.CreateVersion7();
        var largeGroup = Guid.CreateVersion7();

        Commands.Reset();
        await ProcessAsync(Message(smallGroup, alice, "Alice", small, type: ConversationType.Group, title: "Small"));
        var smallStatements = Commands.Count;
        Commands.Reset();
        await ProcessAsync(Message(largeGroup, alice, "Alice", large, type: ConversationType.Group, title: "Large"));

        Commands.Count.ShouldBe(smallStatements);
        (await QueryAsync(db => db.PendingDigests.CountAsync(d => d.ConversationId == largeGroup))).ShouldBe(60);

        Clock.Advance(Delay);
        while (await DispatchAsync() > 0)
        {
        }

        Channel.Sent.Count.ShouldBe(62);
        var groupDigest = Channel.Sent.First(n => n.ConversationId == largeGroup);
        groupDigest.ConversationType.ShouldBe(ConversationType.Group);
        groupDigest.ConversationTitle.ShouldBe("Large");
        Channel.Sent.Where(n => n.ConversationId == largeGroup).Select(n => n.Recipient.UserId).ShouldBe(large, ignoreOrder: true);
    }

    [Fact]
    public async Task MessagesDuringTheCooldown_AccumulateUntilItEnds()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob], "one"));
        Clock.Advance(Delay);
        await DispatchAsync();
        var firstSentAt = Now;

        Clock.Advance(TimeSpan.FromSeconds(10));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], "two"));
        Clock.Advance(TimeSpan.FromMinutes(5));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], "three"));

        (await DigestAsync(bob, chat)).ShouldNotBeNull().DueAtUtc.ShouldBe(firstSentAt + Cooldown);
        Clock.Advance(firstSentAt + Cooldown - Now - TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(0);
        Clock.Advance(TimeSpan.FromSeconds(1));
        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.Count.ShouldBe(2);
        Channel.Sent[1].MessageCount.ShouldBe(2);
        Channel.Sent[1].LastPreview.ShouldBe("three");
    }

    [Fact]
    public async Task OtherConversations_HaveTheirOwnCooldown()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await ProcessAsync(Message(first, alice, "Alice", [bob]));
        Clock.Advance(Delay);
        await DispatchAsync();

        await ProcessAsync(Message(second, alice, "Alice", [bob]));

        (await DigestAsync(bob, second)).ShouldNotBeNull().DueAtUtc.ShouldBe(Now + Delay);
    }

    [Fact]
    public async Task UserDeleted_DropsTheirPendingDigestsAndLaterMessages()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        var group = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        await ProcessAsync(Message(group, alice, "Alice", [bob], type: ConversationType.Group, title: "G"));

        await ProcessAsync(new UserDeleted(Guid.CreateVersion7(), Now, bob));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], "after deletion"));

        (await DigestsAsync()).ShouldBeEmpty();
        (await QueryAsync(db => db.DigestMessages.CountAsync())).ShouldBe(0);
        var tombstone = await QueryAsync(db => db.Recipients.SingleAsync(r => r.UserId == bob));
        tombstone.IsDeleted.ShouldBeTrue();
        tombstone.Email.ShouldBeNull();
        Clock.Advance(Delay);
        (await DispatchAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RecipientUnknownAtSendTime_IsDroppedWithoutSending()
    {
        var alice = await UserAsync("Alice");
        var stranger = Guid.CreateVersion7();
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [stranger]));
        (await DigestAsync(stranger, chat)).ShouldNotBeNull();

        Clock.Advance(Delay);
        (await DispatchAsync()).ShouldBe(1);

        Channel.Sent.ShouldBeEmpty();
        (await DigestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task RecipientChangingTheirAddressBeforeTheDigestIsDue_GetsItAtTheNewAddress()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");
        var chat = Guid.CreateVersion7();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));

        Clock.Advance(TimeSpan.FromSeconds(30));
        await ProcessAsync(new UserProfileUpdated(Guid.CreateVersion7(), Now, bob, "robert@example.com", "Robert"));
        Clock.Advance(Delay);
        await DispatchAsync();

        var recipient = Channel.Sent.ShouldHaveSingleItem().Recipient;
        (recipient.Email, recipient.DisplayName).ShouldBe(("robert@example.com", "Robert"));
    }

    [Fact]
    public async Task MessageOlderThanTheMaximumAge_IsIgnored()
    {
        var alice = await UserAsync("Alice");
        var bob = await UserAsync("Bob");

        await ProcessAsync(Message(Guid.CreateVersion7(), alice, "Alice", [bob], sentAtUtc: Now - TimeSpan.FromHours(25)));

        (await DigestsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task MessagesArrivingOutOfOrder_KeepTheNewestSenderAndPreview()
    {
        var alice = await UserAsync("Alice");
        var carl = await UserAsync("Carl");
        var bob = await UserAsync("Bob");
        var group = Guid.CreateVersion7();
        var older = Message(group, alice, "Alice", [bob], "older", ConversationType.Group, "Team");
        Clock.Advance(TimeSpan.FromSeconds(5));
        var newer = Message(group, carl, "Carl", [bob], "newer", ConversationType.Group, "Team");

        await ProcessAsync(newer);
        await ProcessAsync(older);

        var digest = (await DigestAsync(bob, group)).ShouldNotBeNull();
        (digest.MessageCount, digest.LastSenderName, digest.LastPreview).ShouldBe((2, "Carl", "newer"));
        digest.FirstUnreadAtUtc.ShouldBe(older.SentAtUtc);
        digest.DueAtUtc.ShouldBe(older.SentAtUtc + Delay);
    }
}
