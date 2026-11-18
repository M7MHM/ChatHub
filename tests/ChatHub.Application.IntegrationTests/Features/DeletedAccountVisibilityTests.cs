using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class DeletedAccountVisibilityTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task GetMyConversations_WithDeletedOtherUser_StillRunsOneSqlCommand()
    {
        var (mia, _, _) = await CreateDeletedOwenScenarioAsync();
        Logs.ResetSqlCommands();

        (await SendAsync(mia, new GetMyConversationsQuery())).ShouldSucceed();

        Logs.SqlCommands.ShouldBe(1);
    }

    [Fact]
    public async Task GetMyConversations_DeletedOtherUser_MasksUserTitleAndImage()
    {
        var (mia, owen, direct) = await CreateDeletedOwenScenarioAsync();

        var item = (await SendAsync(mia, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Id == direct);

        ShouldBeMasked(item.OtherUser, owen);
        item.Title.ShouldBe(UserSummary.DeletedDisplayName);
        item.ImageUrl.ShouldBeNull();
    }

    [Fact]
    public async Task GetMyConversations_LastMessageFromDeletedUser_MasksTheSenderName()
    {
        var (mia, _, direct) = await CreateDeletedOwenScenarioAsync();

        var item = (await SendAsync(mia, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Id == direct);

        item.LastMessage.ShouldNotBeNull().SenderName.ShouldBe(UserSummary.DeletedDisplayName);
        item.LastMessage.Text.ShouldBe("bye");
    }

    [Fact]
    public async Task GetConversationDetails_DeletedOtherUser_IsMaskedAndListedLast()
    {
        var (mia, owen, direct) = await CreateDeletedOwenScenarioAsync();

        var details = (await SendAsync(mia, new GetConversationDetailsQuery(direct))).ShouldSucceed();

        ShouldBeMasked(details.OtherUser, owen);
        details.Title.ShouldBe(UserSummary.DeletedDisplayName);
        ShouldBeMasked(details.Members[^1].User, owen);
        details.Members[0].User.Id.ShouldBe(mia);
    }

    [Fact]
    public async Task GetMyBlockedUsers_BlockedUserDeletedTheirAccount_IsMasked()
    {
        var moe = await CreateUserAsync("Moe");
        var (_, owen, _) = await CreateDeletedOwenScenarioAsync(blockedBy: moe);

        var blocked = (await SendAsync(moe, new GetMyBlockedUsersQuery())).ShouldSucceed();

        ShouldBeMasked(blocked.ShouldHaveSingleItem().User, owen);
    }

    private static void ShouldBeMasked(UserSummary? user, Guid userId) =>
        user.ShouldBe(new UserSummary(userId, UserSummary.DeletedDisplayName, null, false, null));

    private async Task<(Guid Mia, Guid Owen, Guid Direct)> CreateDeletedOwenScenarioAsync(Guid? blockedBy = null)
    {
        var owen = await CreateUserAsync("Owen");
        var mia = await CreateUserAsync("Mia");
        var direct = await CreateDirectAsync(mia, owen);
        var avatar = await UploadPngAsync(owen, FilePurpose.Avatar);
        (await SendAsync(owen, new UpdateMyProfileCommand("Owen", null, avatar.Id))).ShouldSucceed();
        await GoOnlineAsync(owen);
        await SendTextAsync(owen, direct, "bye");
        if (blockedBy is { } blocker) await BlockAsync(blocker, owen);

        (await SendAsync(owen, new DeleteMyAccountCommand())).ShouldSucceed();
        return (mia, owen, direct);
    }
}
