using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class ConversationListTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task GetMyConversations_AnyPage_RunsOneSqlCommand()
    {
        var scenario = await CreateListScenarioAsync();
        Logs.ResetSqlCommands();

        (await SendAsync(scenario.Bob, new GetMyConversationsQuery(null, 1))).ShouldSucceed();

        Logs.SqlCommands.ShouldBe(1);
    }

    [Fact]
    public async Task GetMyConversations_FirstPage_ReturnsTheMostRecentConversationAndACursor()
    {
        var scenario = await CreateListScenarioAsync();

        var page = (await SendAsync(scenario.Bob, new GetMyConversationsQuery(null, 1))).ShouldSucceed();

        page.Items.ShouldHaveSingleItem().Id.ShouldBe(scenario.Direct);
        page.HasMore.ShouldBeTrue();
        page.NextCursor.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetMyConversations_Direct_TakesTitleAndImageFromTheOtherUser()
    {
        var scenario = await CreateListScenarioAsync();

        var item = (await SendAsync(scenario.Bob, new GetMyConversationsQuery())).ShouldSucceed()
            .Items.Single(i => i.Id == scenario.Direct);

        item.Title.ShouldBe("Alice A.");
        item.ImageUrl.ShouldBe(FileUrls.PathFor(scenario.AliceAvatarId));
        item.OtherUser.ShouldNotBeNull().Id.ShouldBe(scenario.Alice);
    }

    [Fact]
    public async Task GetMyConversations_Direct_ReturnsUnreadCountAndLastMessagePreview()
    {
        var scenario = await CreateListScenarioAsync();

        var item = (await SendAsync(scenario.Bob, new GetMyConversationsQuery())).ShouldSucceed()
            .Items.Single(i => i.Id == scenario.Direct);

        item.UnreadCount.ShouldBe(1);
        item.LastMessage.ShouldNotBeNull().Text.ShouldBe("hi bob");
        item.LastMessage.SenderName.ShouldBe("Alice A.");
    }

    [Fact]
    public async Task GetMyConversations_SecondPageViaCursor_ReturnsTheOlderConversationAndNoMore()
    {
        var scenario = await CreateListScenarioAsync();
        var first = (await SendAsync(scenario.Bob, new GetMyConversationsQuery(null, 1))).ShouldSucceed();

        var second = (await SendAsync(scenario.Bob, new GetMyConversationsQuery(first.NextCursor, 1))).ShouldSucceed();

        second.Items.ShouldHaveSingleItem().Id.ShouldBe(scenario.Group);
        second.HasMore.ShouldBeFalse();
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task GetMyConversations_Group_UnreadExcludesOwnMessagesAndPreviewIsTruncated()
    {
        var scenario = await CreateListScenarioAsync();

        var item = (await SendAsync(scenario.Bob, new GetMyConversationsQuery())).ShouldSucceed()
            .Items.Single(i => i.Id == scenario.Group);

        item.Title.ShouldBe("Team");
        item.OtherUser.ShouldBeNull();
        item.UnreadCount.ShouldBe(1);
        item.LastMessage.ShouldNotBeNull().Text.Length.ShouldBe(100);
    }

    [Fact]
    public async Task GetMyConversations_Group_CountsEveryMessageFromOthersAsUnread()
    {
        var scenario = await CreateListScenarioAsync();

        var item = (await SendAsync(scenario.Alice, new GetMyConversationsQuery())).ShouldSucceed()
            .Items.Single(i => i.Id == scenario.Group);

        item.UnreadCount.ShouldBe(3);
    }

    [Fact]
    public async Task GetMyConversations_LastMessageDeleted_ShowsDeletedPreviewAndStopsCountingIt()
    {
        var scenario = await CreateListScenarioAsync();

        (await SendAsync(scenario.Bob, new DeleteMessageCommand(scenario.LastGroupMessageId))).ShouldSucceed();

        var item = (await SendAsync(scenario.Alice, new GetMyConversationsQuery())).ShouldSucceed()
            .Items.Single(i => i.Id == scenario.Group);
        item.LastMessage.ShouldNotBeNull().Text.ShouldBe("This message was deleted");
        item.UnreadCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetMyConversations_MessageSentBeforeJoining_HasNoPreviewAndIsNotUnread()
    {
        var (_, mia, nora, group) = await CreateGroupNoraWillJoinAsync();

        await AddMemberAsync(mia, group, nora);

        var item = await ListItemAsync(nora, group);
        item.LastMessage.ShouldBeNull();
        item.UnreadCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetMyConversations_MessageSentAfterJoining_IsPreviewedAndUnread()
    {
        var (max, mia, nora, group) = await CreateGroupNoraWillJoinAsync();
        await AddMemberAsync(mia, group, nora);
        await PauseAsync();

        await SendTextAsync(max, group, "after nora");

        var item = await ListItemAsync(nora, group);
        item.LastMessage.ShouldNotBeNull().Text.ShouldBe("after nora");
        item.UnreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetMyConversations_RejoinAfterClearAndLeave_HidesMessagesSentWhileAway()
    {
        var (max, mia, nora, group) = await CreateGroupNoraWillJoinAsync();
        await AddMemberAsync(mia, group, nora);
        await PauseAsync();
        await SendTextAsync(max, group, "after nora");
        await ClearChatAsync(nora, group);
        (await SendAsync(nora, new LeaveConversationCommand(group))).ShouldSucceed();
        await SendTextAsync(max, group, "while nora was away");
        await PauseAsync();

        await AddMemberAsync(mia, group, nora);

        var item = await ListItemAsync(nora, group);
        item.LastMessage.ShouldBeNull();
        item.UnreadCount.ShouldBe(0);
    }

    private async Task<ConversationListItem> ListItemAsync(Guid userId, Guid conversationId) =>
        (await SendAsync(userId, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Id == conversationId);

    private async Task<(Guid Max, Guid Mia, Guid Nora, Guid Group)> CreateGroupNoraWillJoinAsync()
    {
        var max = await CreateUserAsync("Max");
        var mia = await CreateUserAsync("Mia");
        var nora = await CreateUserAsync("Nora");
        var group = await CreateGroupAsync(mia, "Mia team", max);
        await SendTextAsync(mia, group, "before nora");
        await PauseAsync();
        return (max, mia, nora, group);
    }

    private async Task<ListScenario> CreateListScenarioAsync()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        var carol = await CreateUserAsync("Carol");
        var dave = await CreateUserAsync("Dave");
        var avatar = await UploadPngAsync(alice, FilePurpose.Avatar);
        (await SendAsync(alice, new UpdateMyProfileCommand("Alice A.", null, avatar.Id))).ShouldSucceed();

        var direct = await CreateDirectAsync(alice, bob);
        var group = (await SendAsync(alice, new CreateGroupCommand("Team", null, [bob, carol, dave]))).ShouldSucceed().ConversationId;

        await AddMessageViaDomainAsync(group, bob, MessageType.Text, "hello team");
        await PauseAsync();
        await AddMessageViaDomainAsync(group, carol, MessageType.Image, null,
            Attachment.Create(Guid.CreateVersion7(), "x.png", "image/png", 10));
        await PauseAsync();
        var last = await AddMessageViaDomainAsync(group, bob, MessageType.Text, new string('x', 300));
        await PauseAsync();
        await AddMessageViaDomainAsync(direct, alice, MessageType.Text, "hi bob");

        return new ListScenario(alice, bob, direct, group, avatar.Id, last);
    }

    private sealed record ListScenario(Guid Alice, Guid Bob, Guid Direct, Guid Group, Guid AliceAvatarId, Guid LastGroupMessageId);
}
