using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class UserTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task GetMe_PhoneOnlyUser_ReturnsNormalizedPhoneAndNoEmail()
    {
        var bob = await CreateUserAsync("Bob Builder", phone: "+20 100 123 4567");

        var me = (await SendAsync(bob, new GetMeQuery())).ShouldSucceed();

        me.PhoneNumber.ShouldBe("+201001234567");
        me.Email.ShouldBeNull();
        me.DisplayName.ShouldBe("Bob Builder");
    }

    [Fact]
    public async Task GetMe_WithoutUserId_ThrowsNotAuthenticatedException()
    {
        await Should.ThrowAsync<NotAuthenticatedException>(() => SendAsync(null, new GetMeQuery()));
    }

    [Fact]
    public async Task UpdateMyProfile_ValidRequestWithAvatar_Succeeds()
    {
        var alice = await CreateUserAsync("Alice");
        var avatar = await UploadPngAsync(alice, FilePurpose.Avatar);

        var result = await SendAsync(alice, new UpdateMyProfileCommand("Alice A.", "  hi  ", avatar.Id));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task UpdateMyProfile_ValidRequest_PersistsTrimmedValuesAndKeepsLowercaseEmail()
    {
        var alice = (await RegisterAsync("Alice", "Alice@Example.com")).User.Id;

        (await SendAsync(alice, new UpdateMyProfileCommand("Alice A.", "  hi  "))).ShouldSucceed();

        var me = (await SendAsync(alice, new GetMeQuery())).ShouldSucceed();
        me.DisplayName.ShouldBe("Alice A.");
        me.Bio.ShouldBe("hi");
        me.Email.ShouldBe("alice@example.com");
    }

    [Fact]
    public async Task UpdateMyProfile_MessageAttachmentFileAsAvatar_ReturnsWrongPurpose()
    {
        var alice = await CreateUserAsync("Alice");
        var attachment = await UploadPngAsync(alice);

        var result = await SendAsync(alice, new UpdateMyProfileCommand("Alice", null, attachment.Id));

        result.ShouldFailWith(FileErrors.WrongPurpose);
    }

    [Fact]
    public async Task BlockUser_CalledTwice_SucceedsBothTimes()
    {
        var carol = await CreateUserAsync("Carol");
        var dave = await CreateUserAsync("Dave");

        var first = await SendAsync(carol, new BlockUserCommand(dave));
        var second = await SendAsync(carol, new BlockUserCommand(dave));

        first.ShouldSucceed();
        second.ShouldSucceed();
    }

    [Fact]
    public async Task BlockUser_Self_ReturnsCannotBlockSelf()
    {
        var carol = await CreateUserAsync("Carol");

        var result = await SendAsync(carol, new BlockUserCommand(carol));

        result.ShouldFailWith(UserErrors.CannotBlockSelf);
    }

    [Fact]
    public async Task BlockUser_UnknownUser_ReturnsNotFound()
    {
        var carol = await CreateUserAsync("Carol");

        var result = await SendAsync(carol, new BlockUserCommand(Guid.NewGuid()));

        result.ShouldFailWith(UserErrors.NotFound);
    }

    [Fact]
    public async Task UnblockUser_CalledTwice_SucceedsBothTimes()
    {
        var carol = await CreateUserAsync("Carol");
        var dave = await CreateUserAsync("Dave");
        await BlockAsync(carol, dave);

        var first = await SendAsync(carol, new UnblockUserCommand(dave));
        var second = await SendAsync(carol, new UnblockUserCommand(dave));

        first.ShouldSucceed();
        second.ShouldSucceed();
    }

    [Fact]
    public async Task UnblockUser_BlockedUser_RemovesTheBlock()
    {
        var carol = await CreateUserAsync("Carol");
        var dave = await CreateUserAsync("Dave");
        await BlockAsync(carol, dave);

        (await SendAsync(carol, new UnblockUserCommand(dave))).ShouldSucceed();

        (await SendAsync(carol, new GetMyBlockedUsersQuery())).ShouldSucceed().ShouldBeEmpty();
        (await QueryDbAsync(db => db.UserBlocks.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task SearchUsers_NameSubstring_ReturnsMatchingUser()
    {
        var (alice, bob, _, _) = await CreateFourUsersAsync();

        var result = (await SendAsync(alice, new SearchUsersQuery("bo"))).ShouldSucceed();

        result.ShouldHaveSingleItem().Id.ShouldBe(bob);
    }

    [Fact]
    public async Task SearchUsers_ByUserWhoWasBlocked_HidesTheBlocker()
    {
        var (_, _, carol, dave) = await CreateFourUsersAsync();
        await BlockAsync(carol, dave);

        var result = (await SendAsync(dave, new SearchUsersQuery("carol@example.com"))).ShouldSucceed();

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchUsers_ByBlocker_HidesTheBlockedUser()
    {
        var (_, _, carol, dave) = await CreateFourUsersAsync();
        await BlockAsync(carol, dave);

        var result = (await SendAsync(carol, new SearchUsersQuery("dave"))).ShouldSucceed();

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchUsers_ExactEmailInAnyCase_ReturnsThatUser()
    {
        var (alice, _, carol, _) = await CreateFourUsersAsync();

        var result = (await SendAsync(alice, new SearchUsersQuery("CAROL@example.com"))).ShouldSucceed();

        result.ShouldHaveSingleItem().Id.ShouldBe(carol);
    }

    [Fact]
    public async Task SearchUsers_ExactPhoneNumber_ReturnsThatUser()
    {
        var (alice, bob, _, _) = await CreateFourUsersAsync();

        var result = (await SendAsync(alice, new SearchUsersQuery("+201001234567"))).ShouldSucceed();

        result.ShouldHaveSingleItem().Id.ShouldBe(bob);
    }

    [Fact]
    public async Task SearchUsers_PartialEmail_ReturnsNothing()
    {
        var (alice, _, _, _) = await CreateFourUsersAsync();

        var result = (await SendAsync(alice, new SearchUsersQuery("example.com"))).ShouldSucceed();

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchUsers_SingleCharacterQuery_ThrowsValidationException()
    {
        var (alice, _, _, _) = await CreateFourUsersAsync();

        await Should.ThrowAsync<ValidationException>(() => SendAsync(alice, new SearchUsersQuery("A")));
    }

    [Fact]
    public async Task SearchUsers_QueryMatchingOwnName_ExcludesTheCaller()
    {
        var (alice, _, _, _) = await CreateFourUsersAsync();

        var result = (await SendAsync(alice, new SearchUsersQuery("Ali"))).ShouldSucceed();

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetUserProfile_OnlineUserWhoBlockedTheViewer_HidesPresenceFromTheBlockedViewerOnly()
    {
        var (alice, _, carol, dave) = await CreateFourUsersAsync();
        await BlockAsync(carol, dave);
        await GoOnlineAsync(carol);

        var carolFromDave = (await SendAsync(dave, new GetUserProfileQuery(carol))).ShouldSucceed();
        var carolFromAlice = (await SendAsync(alice, new GetUserProfileQuery(carol))).ShouldSucceed();

        carolFromDave.IsOnline.ShouldBeFalse();
        carolFromDave.LastSeenUtc.ShouldBeNull();
        carolFromAlice.IsOnline.ShouldBeTrue();
    }

    [Fact]
    public async Task GetUserProfile_IsBlockedByMe_ReflectsTheViewersOwnBlocks()
    {
        var (alice, _, carol, dave) = await CreateFourUsersAsync();
        await BlockAsync(carol, dave);

        var daveFromCarol = (await SendAsync(carol, new GetUserProfileQuery(dave))).ShouldSucceed();
        var carolFromAlice = (await SendAsync(alice, new GetUserProfileQuery(carol))).ShouldSucceed();

        daveFromCarol.IsBlockedByMe.ShouldBeTrue();
        carolFromAlice.IsBlockedByMe.ShouldBeFalse();
    }

    [Fact]
    public async Task GetMyBlockedUsers_AfterBlocking_ListsTheBlockedUser()
    {
        var (_, _, carol, dave) = await CreateFourUsersAsync();
        await BlockAsync(carol, dave);

        var blocked = (await SendAsync(carol, new GetMyBlockedUsersQuery())).ShouldSucceed();

        blocked.ShouldHaveSingleItem().User.Id.ShouldBe(dave);
    }

    [Fact]
    public async Task UserSummaryFrom_DeletedUser_IsMasked()
    {
        var owen = await CreateUserAsync("Owen");
        var avatar = await UploadPngAsync(owen, FilePurpose.Avatar);
        (await SendAsync(owen, new UpdateMyProfileCommand("Owen", null, avatar.Id))).ShouldSucceed();
        await GoOnlineAsync(owen);
        (await SendAsync(owen, new DeleteMyAccountCommand())).ShouldSucceed();
        var stored = await QueryDbAsync(db => db.UserProfiles.AsNoTracking().FirstAsync(u => u.Id == owen));

        var summary = UserSummary.From(stored);

        summary.ShouldBe(new UserSummary(owen, null, UserSummary.DeletedDisplayName, null, false, null));
    }

    private async Task<(Guid Alice, Guid Bob, Guid Carol, Guid Dave)> CreateFourUsersAsync() =>
        (await CreateUserAsync("Alice"),
            await CreateUserAsync("Bob Builder", phone: "+20 100 123 4567"),
            await CreateUserAsync("Carol"),
            await CreateUserAsync("Dave"));
}
