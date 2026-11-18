using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Messages.EventHandlers;
using ChatHub.Application.Features.Messages.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class ReadStateTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task MarkAsRead_NewestMessage_Succeeds()
    {
        var h = await CreateCrewHistoryAsync();

        var result = await MarkReadAsync(h.Crew.Quinn, h.Crew.Group, h.TessHi);

        result.ShouldSucceed();
    }

    [Fact]
    public async Task MarkAsRead_NewerThanBothPointers_BroadcastsMessagesReadAndMessagesDelivered()
    {
        var h = await CreateCrewHistoryAsync();
        var mark = SignalR.Mark();

        (await MarkReadAsync(h.Crew.Quinn, h.Crew.Group, h.TessHi)).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        Guid[] members = [h.Crew.Paula, h.Crew.Quinn, h.Crew.Rex, h.Crew.Tess];
        deliveries.Count.ShouldBe(8);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessagesRead, members);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessagesDelivered, members);
        var readPayloads = deliveries.Where(d => d.Method == ChatEvents.MessagesRead).Select(d => d.Payload).ToList();
        readPayloads.ShouldAllBe(p => p is MessagesReadPayload);
        readPayloads.Cast<MessagesReadPayload>().ShouldAllBe(p =>
            p.ConversationId == h.Crew.Group && p.UserId == h.Crew.Quinn && p.MessageId == h.TessHi);
        deliveries.Where(d => d.Method == ChatEvents.MessagesDelivered)
            .ShouldAllBe(d => Equals(d.Payload, new MessagesDeliveredPayload(h.Crew.Group, h.Crew.Quinn, h.TessHi)));
    }

    [Fact]
    public async Task MarkAsRead_OlderThanTheCurrentPointer_SucceedsWithoutEvents()
    {
        var h = await CreateCrewHistoryAsync();
        (await MarkReadAsync(h.Crew.Quinn, h.Crew.Group, h.TessHi)).ShouldSucceed();
        var mark = SignalR.Mark();

        var result = await MarkReadAsync(h.Crew.Quinn, h.Crew.Group, h.Hello);

        result.ShouldSucceed();
        SignalR.Since(mark).ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkAsDelivered_NewerMessage_BroadcastsMessagesDeliveredOnly()
    {
        var h = await CreateCrewHistoryAsync();
        var mark = SignalR.Mark();

        (await MarkDeliveredAsync(h.Crew.Rex, h.Crew.Group, h.LateReply)).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(4);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessagesDelivered, h.Crew.Paula, h.Crew.Quinn, h.Crew.Rex, h.Crew.Tess);
    }

    [Fact]
    public async Task MarkAsRead_AlreadyDeliveredMessage_BroadcastsMessagesReadOnly()
    {
        var h = await CreateCrewHistoryAsync();
        (await MarkDeliveredAsync(h.Crew.Rex, h.Crew.Group, h.LateReply)).ShouldSucceed();
        var mark = SignalR.Mark();

        (await MarkReadAsync(h.Crew.Rex, h.Crew.Group, h.LateReply)).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(4);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessagesRead, h.Crew.Paula, h.Crew.Quinn, h.Crew.Rex, h.Crew.Tess);
    }

    [Fact]
    public async Task MarkMessage_HiddenOrFromAnotherConversation_ReturnsNotFound()
    {
        var h = await CreateCrewHistoryAsync();
        var privateMessage = await SendTextAsync(h.Crew.Paula, h.Crew.Direct, "private");

        var readBeforeJoin = await MarkReadAsync(h.Crew.Tess, h.Crew.Group, h.Hello);
        var readForeign = await MarkReadAsync(h.Crew.Quinn, h.Crew.Group, privateMessage.Id);
        var deliveredBeforeJoin = await MarkDeliveredAsync(h.Crew.Tess, h.Crew.Group, h.Hello);

        readBeforeJoin.ShouldFailWith(MessageErrors.NotFound);
        readForeign.ShouldFailWith(MessageErrors.NotFound);
        deliveredBeforeJoin.ShouldFailWith(MessageErrors.NotFound);
    }

    [Fact]
    public async Task GetReadState_Group_ListsEveryActiveMembersPointers()
    {
        var h = await CreateCrewHistoryAsync();
        (await MarkReadAsync(h.Crew.Quinn, h.Crew.Group, h.TessHi)).ShouldSucceed();
        (await MarkDeliveredAsync(h.Crew.Rex, h.Crew.Group, h.LateReply)).ShouldSucceed();
        (await MarkReadAsync(h.Crew.Rex, h.Crew.Group, h.LateReply)).ShouldSucceed();

        var states = (await SendAsync(h.Crew.Paula, new GetReadStateQuery(h.Crew.Group))).ShouldSucceed();

        states.Count.ShouldBe(4);
        states.Single(s => s.UserId == h.Crew.Quinn).LastReadMessageId.ShouldBe(h.TessHi);
        var rex = states.Single(s => s.UserId == h.Crew.Rex);
        rex.LastReadMessageAtUtc.ShouldNotBeNull();
        rex.LastReadMessageId.ShouldBe(h.LateReply);
        rex.LastDeliveredMessageId.ShouldBe(h.LateReply);
        states.Single(s => s.UserId == h.Crew.Tess).LastReadMessageId.ShouldBeNull();
    }

    [Fact]
    public async Task GetReadState_NonMember_ReturnsNotMember()
    {
        var crew = await CreateCrewAsync();

        var result = await SendAsync(crew.Sam, new GetReadStateQuery(crew.Group));

        result.ShouldFailWith(ConversationErrors.NotMember);
    }

    [Fact]
    public async Task MarkOwnMessage_ReadAndDelivered_Succeeds()
    {
        var (vera, _, direct, _, fromVera) = await CreateVeraAndWaltChatAsync();

        var read = await MarkReadAsync(vera, direct, fromVera);
        var delivered = await MarkDeliveredAsync(vera, direct, fromVera);

        read.ShouldSucceed();
        delivered.ShouldSucceed();
    }

    [Fact]
    public async Task MarkOwnMessage_ReadAndDelivered_LeavesPointersAndRaisesNoEvent()
    {
        var (vera, _, direct, _, fromVera) = await CreateVeraAndWaltChatAsync();
        var mark = SignalR.Mark();

        (await MarkReadAsync(vera, direct, fromVera)).ShouldSucceed();
        (await MarkDeliveredAsync(vera, direct, fromVera)).ShouldSucceed();

        var member = await QueryDbAsync(db => db.ConversationMembers.AsNoTracking()
            .FirstAsync(m => m.ConversationId == direct && m.UserId == vera));
        member.LastReadMessageId.ShouldBeNull();
        member.LastDeliveredMessageId.ShouldBeNull();
        SignalR.Since(mark).ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkOwnMessage_AsRead_KeepsTheOtherUsersEarlierMessageUnread()
    {
        var (vera, _, direct, _, fromVera) = await CreateVeraAndWaltChatAsync();

        (await MarkReadAsync(vera, direct, fromVera)).ShouldSucceed();

        var item = (await SendAsync(vera, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Id == direct);
        item.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task MarkAsRead_OtherUsersMessage_ClearsUnreadAndNotifiesBothMembers()
    {
        var (vera, _, direct, fromWalt, fromVera) = await CreateVeraAndWaltChatAsync();
        (await MarkReadAsync(vera, direct, fromVera)).ShouldSucceed();
        var mark = SignalR.Mark();

        (await MarkReadAsync(vera, direct, fromWalt)).ShouldSucceed();

        SignalR.Since(mark).Count(d => d.Method == ChatEvents.MessagesRead).ShouldBe(2);
        var item = (await SendAsync(vera, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Id == direct);
        item.UnreadCount.ShouldBe(0);
    }

    private Task<Result> MarkReadAsync(Guid userId, Guid conversationId, Guid messageId) =>
        SendAsync(userId, new MarkAsReadCommand(messageId) { ConversationId = conversationId });

    private Task<Result> MarkDeliveredAsync(Guid userId, Guid conversationId, Guid messageId) =>
        SendAsync(userId, new MarkAsDeliveredCommand(messageId) { ConversationId = conversationId });

    private async Task<(Guid Vera, Guid Walt, Guid Direct, Guid FromWalt, Guid FromVera)> CreateVeraAndWaltChatAsync()
    {
        var vera = await CreateUserAsync("Vera");
        var walt = await CreateUserAsync("Walt");
        var direct = await CreateDirectAsync(vera, walt);
        var fromWalt = await SendTextAsync(walt, direct, "unread for vera");
        var fromVera = await SendTextAsync(vera, direct, "mine");
        return (vera, walt, direct, fromWalt.Id, fromVera.Id);
    }
}
