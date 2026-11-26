using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class UsernameTests(DatabaseFixture database) : AppTestBase(database)
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromDays(14);

    [Fact]
    public async Task Register_WithAUsername_StoresItLowercaseAndReturnsItInTheResponseAndGetMe()
    {
        var auth = await RegisterAsAsync("Alice", "Alice_99");

        auth.User.Username.ShouldBe("alice_99");
        var me = (await SendAsync(auth.User.Id, new GetMeQuery())).ShouldSucceed();
        (me.Username, me.UsernameChangeAvailableAtUtc, me.ShowLastSeen).ShouldBe(("alice_99", (DateTime?)null, true));
        (await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == auth.User.Id).Select(u => u.Username).SingleAsync()))
            .ShouldBe(Username.Create("alice_99"));
    }

    [Fact]
    public async Task Register_UsernameOfAnActiveUserInAnotherCase_ReturnsUsernameTaken()
    {
        await RegisterAsAsync("Alice", "alice");

        var result = await SendAsync(null, new RegisterCommand("Other", "ALICE", "other@example.com", null, Password));

        result.ShouldFailWith(UserErrors.UsernameTaken);
    }

    [Fact]
    public async Task Register_UsernameOfADeletedAccount_IsFreed()
    {
        var gone = await RegisterAsAsync("Gone", "freed_name", "gone@example.com");
        (await SendAsync(gone.User.Id, new DeleteMyAccountCommand())).ShouldSucceed();

        var again = await RegisterAsAsync("New Owner", "freed_name", "new@example.com");

        again.User.Username.ShouldBe("freed_name");
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.Username == Username.Create("freed_name")))).ShouldBe(2);
    }

    [Theory]
    [InlineData("ab", Username.RulesMessage)]
    [InlineData("1abc", Username.RulesMessage)]
    [InlineData("no..dots", Username.RulesMessage)]
    [InlineData("Admin", Username.ReservedMessage)]
    [InlineData("guest", Username.ReservedMessage)]
    public async Task Register_InvalidOrReservedUsername_ThrowsValidationExceptionWithTheRule(string username, string message)
    {
        var exception = await Should.ThrowAsync<ValidationException>(() =>
            SendAsync(null, new RegisterCommand("X", username, "x@example.com", null, Password)));

        exception.Errors["Username"].ShouldBe([message]);
    }

    [Fact]
    public async Task Register_MissingUsername_ThrowsValidationException()
    {
        var exception = await Should.ThrowAsync<ValidationException>(() =>
            SendAsync(null, new RegisterCommand("X", "", "x@example.com", null, Password)));

        exception.Errors.Keys.ShouldBe(["Username"]);
    }

    [Fact]
    public async Task Register_ConcurrentWithTheSameUsername_OneSucceedsAndTheOtherGetsUsernameTaken()
    {
        var (a, b) = await RaceAsync(
            () => SendAsync(null, new RegisterCommand("Twin A", "twin", "twin.a@example.com", null, Password)),
            () => SendAsync(null, new RegisterCommand("Twin B", "TWIN", "twin.b@example.com", null, Password)));

        new[] { a, b }.Count(r => r.IsSuccess).ShouldBe(1);
        new[] { a, b }.Single(r => r.IsFailure).Error.ShouldBe(UserErrors.UsernameTaken);
    }

    [Fact]
    public async Task ChangeUsername_FirstChange_SucceedsAndGetMeShowsItAfterAnEarlierCachedRead()
    {
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;
        (await SendAsync(alice, new GetMeQuery())).ShouldSucceed().Username.ShouldBe("alice");

        var changed = (await SendAsync(alice, new ChangeUsernameCommand("Alice.Smith"))).ShouldSucceed();

        var now = Clock.GetUtcNow().UtcDateTime;
        changed.ShouldBe(new UsernameResponse("alice.smith", changed.NextChangeAtUtc));
        changed.NextChangeAtUtc.ShouldNotBeNull().ShouldBe(now + Cooldown, TimeSpan.FromMilliseconds(1));
        var me = (await SendAsync(alice, new GetMeQuery())).ShouldSucceed();
        me.Username.ShouldBe("alice.smith");
        me.UsernameChangeAvailableAtUtc.ShouldNotBeNull().ShouldBe(now + Cooldown, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ChangeUsername_WithinTheCooldown_ReturnsTooSoonUntilItEnds()
    {
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;
        (await SendAsync(alice, new ChangeUsernameCommand("alice_one"))).ShouldSucceed();
        var nextChange = Clock.GetUtcNow().UtcDateTime + Cooldown;

        Clock.Advance(Cooldown - TimeSpan.FromMinutes(1));
        var tooSoon = await SendAsync(alice, new ChangeUsernameCommand("alice_two"));
        var stored = await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == alice).Select(u => u.UsernameChangedAtUtc).SingleAsync());

        tooSoon.IsFailure.ShouldBeTrue();
        tooSoon.Error.Code.ShouldBe(UserErrors.UsernameChangeTooSoonCode);
        tooSoon.Error.ShouldBe(UserErrors.UsernameChangeTooSoon(stored!.Value + Cooldown));
        stored.Value.ShouldBe(nextChange - Cooldown, TimeSpan.FromMilliseconds(1));

        Clock.Advance(TimeSpan.FromMinutes(1));
        (await SendAsync(alice, new ChangeUsernameCommand("alice_two"))).ShouldSucceed().Username.ShouldBe("alice_two");
    }

    [Fact]
    public async Task ChangeUsername_ToTheCurrentNameInAnotherCase_SucceedsWithoutStartingTheCooldown()
    {
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;

        (await SendAsync(alice, new ChangeUsernameCommand(" ALICE "))).ShouldSucceed().NextChangeAtUtc.ShouldBeNull();

        (await SendAsync(alice, new ChangeUsernameCommand("alice_new"))).ShouldSucceed();
    }

    [Fact]
    public async Task ChangeUsername_TakenInvalidOrReserved_ReturnsTheMatchingErrorAndChangesNothing()
    {
        await RegisterAsAsync("Bob", "bob");
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;

        (await SendAsync(alice, new ChangeUsernameCommand("BOB"))).ShouldFailWith(UserErrors.UsernameTaken);
        (await SendAsync(alice, new ChangeUsernameCommand("b"))).ShouldFailWith(UserErrors.UsernameInvalid);
        (await SendAsync(alice, new ChangeUsernameCommand(""))).ShouldFailWith(UserErrors.UsernameInvalid);
        (await SendAsync(alice, new ChangeUsernameCommand("Support"))).ShouldFailWith(UserErrors.UsernameReserved);

        var me = (await SendAsync(alice, new GetMeQuery())).ShouldSucceed();
        (me.Username, me.UsernameChangeAvailableAtUtc).ShouldBe(("alice", (DateTime?)null));
    }

    [Fact]
    public async Task ChangeUsername_ToTheNameOfADeletedAccount_Succeeds()
    {
        var gone = (await RegisterAsAsync("Gone", "old_name", "gone@example.com")).User.Id;
        (await SendAsync(gone, new DeleteMyAccountCommand())).ShouldSucceed();
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;

        (await SendAsync(alice, new ChangeUsernameCommand("old_name"))).ShouldSucceed();
    }

    [Fact]
    public async Task ChangeUsername_Success_SendsProfileUpdatedWithTheNewUsernameToPeers()
    {
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;
        var bob = await CreateUserAsync("Bob");
        await CreateDirectAsync(alice, bob);
        var mark = SignalR.Mark();

        (await SendAsync(alice, new ChangeUsernameCommand("alice_renamed"))).ShouldSucceed();

        SignalR.Since(mark).OnlyPayload(bob, ChatEvents.ProfileUpdated).ShouldBeOfType<UserSummary>()
            .Username.ShouldBe("alice_renamed");
    }

    [Theory]
    [InlineData("fresh_name", "fresh_name", true, null)]
    [InlineData("Taken_Name", "taken_name", false, UsernameAvailabilityResponse.Taken)]
    [InlineData("x", "x", false, UsernameAvailabilityResponse.Invalid)]
    [InlineData(" Has Space ", "has space", false, UsernameAvailabilityResponse.Invalid)]
    [InlineData("ROOT", "root", false, UsernameAvailabilityResponse.Reserved)]
    [InlineData("deleted_owner", "deleted_owner", true, null)]
    public async Task CheckAvailability_Anonymous_ReportsAvailabilityAndTheReason(string input, string normalized, bool available,
        string? reason)
    {
        await RegisterAsAsync("Taken", "taken_name");
        var gone = (await RegisterAsAsync("Gone", "deleted_owner", "gone@example.com")).User.Id;
        (await SendAsync(gone, new DeleteMyAccountCommand())).ShouldSucceed();

        var result = (await SendAsync(null, new CheckUsernameAvailabilityQuery(input))).ShouldSucceed();

        result.ShouldBe(new UsernameAvailabilityResponse(normalized, available, reason));
    }

    [Fact]
    public async Task CheckAvailability_OwnUsernameWhenSignedIn_IsAvailableButTakenForOthers()
    {
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;
        var bob = await CreateUserAsync("Bob");

        (await SendAsync(alice, new CheckUsernameAvailabilityQuery("alice"))).ShouldSucceed().Available.ShouldBeTrue();
        (await SendAsync(bob, new CheckUsernameAvailabilityQuery("alice"))).ShouldSucceed().Available.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckAvailability_UsernameOfAUserWhoBlockedMe_SaysTakenWithoutAnythingElse()
    {
        var blocker = (await RegisterAsAsync("Blocker", "blocker")).User.Id;
        var me = await CreateUserAsync("Me");
        await BlockAsync(blocker, me);

        var result = (await SendAsync(me, new CheckUsernameAvailabilityQuery("blocker"))).ShouldSucceed();

        result.ShouldBe(new UsernameAvailabilityResponse("blocker", false, UsernameAvailabilityResponse.Taken));
    }

    [Theory]
    [InlineData("alice_login")]
    [InlineData("Alice_Login")]
    [InlineData("  alice_login ")]
    public async Task Login_ByUsernameInAnyCase_SignsInThatUser(string identifier)
    {
        var alice = await RegisterAsAsync("Alice", "alice_login");

        var session = await LoginAsync(identifier);

        session.User.Id.ShouldBe(alice.User.Id);
        session.User.Username.ShouldBe("alice_login");
    }

    [Fact]
    public async Task Login_UnknownOrDeletedUsername_FailsAfterTheSameHashingWork()
    {
        var gone = (await RegisterAsAsync("Gone", "gone_user", "gone@example.com")).User.Id;
        (await SendAsync(gone, new DeleteMyAccountCommand())).ShouldSucceed();
        var before = Hasher.VerifiedHashes.Count;

        (await SendAsync(null, new LoginCommand("nobody_here", Password))).ShouldFailWith(AuthErrors.InvalidCredentials);
        (await SendAsync(null, new LoginCommand("gone_user", Password))).ShouldFailWith(AuthErrors.InvalidCredentials);

        Hasher.VerifiedHashes.Count.ShouldBe(before + 2);
    }

    [Fact]
    public async Task Search_ByExactUsername_ListsThatUserFirst()
    {
        var me = await CreateUserAsync("Me");
        await RegisterAsAsync("Aaron Sam", "sam_fan");
        var sam = (await RegisterAsAsync("Zed", "sam")).User.Id;

        var results = (await SendAsync(me, new SearchUsersQuery("sam"))).ShouldSucceed();

        results.Select(u => u.Username).ShouldBe(["sam", "sam_fan"]);
        results[0].Id.ShouldBe(sam);
    }

    [Theory]
    [InlineData("@jo")]
    [InlineData("JOHN.")]
    [InlineData("john.d")]
    public async Task Search_ByUsernamePrefix_FindsUsersWhoseNameDoesNotMatch(string term)
    {
        var me = await CreateUserAsync("Me");
        var john = (await RegisterAsAsync("Zed", "john.doe")).User.Id;

        var results = (await SendAsync(me, new SearchUsersQuery(term))).ShouldSucceed();

        results.ShouldHaveSingleItem().Id.ShouldBe(john);
    }

    [Fact]
    public async Task Search_ByUsername_SkipsBlockedAndDeletedUsersAndMyself()
    {
        var me = (await RegisterAsAsync("Me", "match_me")).User.Id;
        var blocked = (await RegisterAsAsync("B", "match_blocked")).User.Id;
        var gone = (await RegisterAsAsync("G", "match_gone", "gone@example.com")).User.Id;
        var visible = (await RegisterAsAsync("V", "match_visible")).User.Id;
        await BlockAsync(blocked, me);
        (await SendAsync(gone, new DeleteMyAccountCommand())).ShouldSucceed();

        var results = (await SendAsync(me, new SearchUsersQuery("match"))).ShouldSucceed();

        results.ShouldHaveSingleItem().Id.ShouldBe(visible);
    }

    [Fact]
    public async Task ConversationListAndMembers_IncludeTheUsernameAndNoneForADeletedAccount()
    {
        var alice = (await RegisterAsAsync("Alice", "alice")).User.Id;
        var bob = (await RegisterAsAsync("Bob", "bob")).User.Id;
        var carol = (await RegisterAsAsync("Carol", "carol", "carol@example.com")).User.Id;
        var withBob = await CreateDirectAsync(alice, bob);
        var withCarol = await CreateDirectAsync(alice, carol);
        var group = await CreateGroupAsync(alice, "Team", bob);
        (await SendAsync(carol, new DeleteMyAccountCommand())).ShouldSucceed();

        var list = (await SendAsync(alice, new GetMyConversationsQuery())).ShouldSucceed();
        var details = (await SendAsync(alice, new GetConversationDetailsQuery(group))).ShouldSucceed();

        list.Items.Single(i => i.Id == withBob).OtherUser.ShouldNotBeNull().Username.ShouldBe("bob");
        var deleted = list.Items.Single(i => i.Id == withCarol).OtherUser.ShouldNotBeNull();
        (deleted.Username, deleted.DisplayName).ShouldBe(((string?)null, UserSummary.DeletedDisplayName));
        details.Members.Select(m => m.User.Username).ShouldBe(["alice", "bob"], ignoreOrder: true);
    }

    private async Task<AuthResponse> RegisterAsAsync(string displayName, string username, string? email = null) =>
        (await SendAsync(null, new RegisterCommand(displayName, username,
            email ?? $"{Guid.NewGuid():N}@example.com", null, Password))).ShouldSucceed();
}
