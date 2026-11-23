using ChatHub.Contracts.Events;
using ChatHub.Notifications.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Notifications.Tests;

public sealed class ConversationEventTests(NotificationsFixture fixture) : NotificationsTestBase(fixture)
{
    [Fact]
    public async Task MessageDeleted_TheOnlyMessage_CancelsTheDigest()
    {
        var (alice, bob, chat) = await PairAsync();
        var sent = Message(chat, alice, "Alice", [bob], preview: "oops, wrong chat");
        await ProcessAsync(sent);

        await ProcessAsync(Deleted(sent));

        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.DigestMessages.CountAsync())).ShouldBe(0);
        Clock.Advance(Delay);
        await DispatchAsync();
        Channel.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task MessageDeleted_TheLatestOfSeveral_FallsBackToThePreviousSenderAndPreview()
    {
        var (alice, bob, carol, group) = await GroupAsync();
        await ProcessAsync(Message(group, carol, "Carol", [alice, bob], preview: "lunch?", type: ConversationType.Group, title: "Team"));
        var firstAt = Now;
        Clock.Advance(TimeSpan.FromSeconds(10));
        var secret = Message(group, alice, "Alice", [bob, carol], preview: "my password is hunter2", type: ConversationType.Group,
            title: "Team");
        await ProcessAsync(secret);

        await ProcessAsync(Deleted(secret));

        var digest = (await DigestAsync(bob, group)).ShouldNotBeNull();
        (digest.MessageCount, digest.LastSenderName, digest.LastPreview, digest.LastMessageAtUtc).ShouldBe((1, "Carol", "lunch?", firstAt));
        Clock.Advance(Delay);
        await DispatchAsync();
        Channel.Sent.Where(n => n.Recipient.UserId == bob).ShouldHaveSingleItem().LastPreview.ShouldBe("lunch?");
        Channel.Sent.ShouldAllBe(n => n.LastPreview != "my password is hunter2");
    }

    [Fact]
    public async Task MessageDeleted_TheFirstOfTwo_RestartsTheClockAtTheRemainingMessage()
    {
        var (alice, bob, chat) = await PairAsync();
        var first = Message(chat, alice, "Alice", [bob], preview: "first");
        await ProcessAsync(first);
        Clock.Advance(TimeSpan.FromSeconds(60));
        await ProcessAsync(Message(chat, alice, "Alice", [bob], preview: "second"));

        await ProcessAsync(Deleted(first));

        var digest = (await DigestAsync(bob, chat)).ShouldNotBeNull();
        (digest.MessageCount, digest.FirstUnreadAtUtc, digest.DueAtUtc).ShouldBe((1, Now, Now + Delay));
    }

    [Fact]
    public async Task MessageDeleted_BeforeItsMessageSentArrives_IsNeverDigested()
    {
        var (alice, bob, chat) = await PairAsync();
        var sent = Message(chat, alice, "Alice", [bob], preview: "deleted right away");

        await ProcessAsync(Deleted(sent));
        await ProcessAsync(sent);

        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.DigestMessages.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task MessageDeleted_DeliveredTwiceOrForAMessageWithoutDigests_IsHarmless()
    {
        var (alice, bob, chat) = await PairAsync();
        var kept = Message(chat, alice, "Alice", [bob], preview: "kept");
        await ProcessAsync(kept);
        var deletion = Deleted(Message(chat, alice, "Alice", [bob], preview: "never seen"));

        (await ProcessAsync(deletion)).ShouldBeTrue();
        (await ProcessAsync(deletion)).ShouldBeFalse();

        (await DigestAsync(bob, chat)).ShouldNotBeNull().LastPreview.ShouldBe("kept");
    }

    [Fact]
    public async Task MessageEdited_TheLatestMessage_UpdatesThePreviewButAnOlderOneDoesNot()
    {
        var (alice, bob, chat) = await PairAsync();
        var first = Message(chat, alice, "Alice", [bob], preview: "first");
        await ProcessAsync(first);
        Clock.Advance(TimeSpan.FromSeconds(5));
        var second = Message(chat, alice, "Alice", [bob], preview: "see you at 5");
        await ProcessAsync(second);

        await ProcessAsync(Edited(first, "first, edited", Now.AddSeconds(1)));
        (await DigestAsync(bob, chat)).ShouldNotBeNull().LastPreview.ShouldBe("see you at 5");
        await ProcessAsync(Edited(second, "see you at 6", Now.AddSeconds(2)));

        (await DigestAsync(bob, chat)).ShouldNotBeNull().LastPreview.ShouldBe("see you at 6");
        Clock.Advance(Delay);
        await DispatchAsync();
        Channel.Sent.ShouldHaveSingleItem().LastPreview.ShouldBe("see you at 6");
    }

    [Fact]
    public async Task MessageEdited_BeforeItsMessageSentAndOutOfOrder_KeepsTheNewestText()
    {
        var (alice, bob, chat) = await PairAsync();
        var sent = Message(chat, alice, "Alice", [bob], preview: "draft");

        await ProcessAsync(Edited(sent, "final", Now.AddSeconds(20)));
        await ProcessAsync(sent);
        await ProcessAsync(Edited(sent, "middle", Now.AddSeconds(10)));

        (await DigestAsync(bob, chat)).ShouldNotBeNull().LastPreview.ShouldBe("final");
    }

    [Fact]
    public async Task MessageEdited_AfterTheMessageWasDeleted_DoesNotBringItBack()
    {
        var (alice, bob, chat) = await PairAsync();
        var sent = Message(chat, alice, "Alice", [bob], preview: "original");
        await ProcessAsync(sent);
        await ProcessAsync(Deleted(sent));

        await ProcessAsync(Edited(sent, "edited after delete", Now.AddSeconds(5)));

        (await DigestAsync(bob, chat)).ShouldBeNull();
        (await QueryAsync(db => db.MessageSnapshots.SingleAsync(s => s.ChatMessageId == sent.ChatMessageId))).Preview.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberRemoved_DropsTheDigestIgnoresOlderMessagesArrivingLateButNotMessagesAfterARejoin(bool kicked)
    {
        var (alice, bob, carol, group) = await GroupAsync();
        await ProcessAsync(Message(group, alice, "Alice", [bob, carol], type: ConversationType.Group, title: "Team"));
        Clock.Advance(TimeSpan.FromSeconds(5));
        var late = Message(group, alice, "Alice", [bob, carol], type: ConversationType.Group, title: "Team");
        Clock.Advance(TimeSpan.FromSeconds(5));

        await ProcessAsync(kicked
            ? new MemberKicked(Guid.CreateVersion7(), Now, group, bob, alice)
            : new MemberLeft(Guid.CreateVersion7(), Now, group, bob));
        await ProcessAsync(late);

        (await DigestAsync(bob, group)).ShouldBeNull();
        (await DigestAsync(carol, group)).ShouldNotBeNull().MessageCount.ShouldBe(2);

        Clock.Advance(TimeSpan.FromMinutes(1));
        await ProcessAsync(Message(group, alice, "Alice", [bob, carol], type: ConversationType.Group, title: "Team"));
        (await DigestAsync(bob, group)).ShouldNotBeNull().MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task Mute_DropsThePendingDigestAndALateOlderMessage_UnmuteAffectsOnlyNewMessages()
    {
        var (alice, bob, chat) = await PairAsync();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        Clock.Advance(TimeSpan.FromSeconds(5));
        var late = Message(chat, alice, "Alice", [bob]);
        Clock.Advance(TimeSpan.FromSeconds(5));

        await ProcessAsync(new ConversationMuteChanged(Guid.CreateVersion7(), Now, chat, bob, IsMuted: true));
        await ProcessAsync(late);
        (await DigestAsync(bob, chat)).ShouldBeNull();

        await ProcessAsync(Message(chat, alice, "Alice", [bob], muted: [bob]));
        (await DigestAsync(bob, chat)).ShouldBeNull();

        Clock.Advance(TimeSpan.FromSeconds(10));
        await ProcessAsync(new ConversationMuteChanged(Guid.CreateVersion7(), Now, chat, bob, IsMuted: false));
        (await DigestAsync(bob, chat)).ShouldBeNull();
        Clock.Advance(TimeSpan.FromSeconds(1));
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        (await DigestAsync(bob, chat)).ShouldNotBeNull().MessageCount.ShouldBe(1);
    }

    [Fact]
    public async Task Mute_ArrivingAfterALaterUnmute_StillDropsOnlyWhatCameBeforeTheMute()
    {
        var (alice, bob, chat) = await PairAsync();
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        var mutedAt = Now.AddSeconds(5);
        Clock.Advance(TimeSpan.FromSeconds(20));

        await ProcessAsync(new ConversationMuteChanged(Guid.CreateVersion7(), Now, chat, bob, IsMuted: false));
        await ProcessAsync(Message(chat, alice, "Alice", [bob]));
        await ProcessAsync(new ConversationMuteChanged(Guid.CreateVersion7(), mutedAt, chat, bob, IsMuted: true));

        (await DigestAsync(bob, chat)).ShouldNotBeNull().MessageCount.ShouldBe(1);
    }

    private static MessageDeleted Deleted(MessageSent message) =>
        new(Guid.CreateVersion7(), message.SentAtUtc.AddSeconds(1), message.ChatMessageId, message.ConversationId);

    private static MessageEdited Edited(MessageSent message, string preview, DateTime editedAtUtc) =>
        new(Guid.CreateVersion7(), editedAtUtc, message.ChatMessageId, message.ConversationId, preview, editedAtUtc);

    private async Task<(Guid Alice, Guid Bob, Guid Chat)> PairAsync() =>
        (await UserAsync("Alice"), await UserAsync("Bob"), Guid.CreateVersion7());

    private async Task<(Guid Alice, Guid Bob, Guid Carol, Guid Group)> GroupAsync() =>
        (await UserAsync("Alice"), await UserAsync("Bob"), await UserAsync("Carol"), Guid.CreateVersion7());
}
