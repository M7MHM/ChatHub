using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class AccountDeletionTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task DeleteMyAccount_ActiveUser_Succeeds()
    {
        var dave = await CreateUserAsync("Dave");

        var result = await SendAsync(dave, new DeleteMyAccountCommand());

        result.ShouldSucceed();
    }

    [Fact]
    public async Task DeleteMyAccount_Success_UserCanNoLongerLogIn()
    {
        var dave = await CreateUserAsync("Dave");

        (await SendAsync(dave, new DeleteMyAccountCommand())).ShouldSucceed();

        (await SendAsync(null, new LoginCommand("dave@example.com", Password))).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteMyAccount_Success_RevokesRefreshTokens()
    {
        var dave = await CreateUserAsync("Dave");
        var session = await LoginAsync("dave@example.com");

        (await SendAsync(dave, new DeleteMyAccountCommand())).ShouldSucceed();

        (await RefreshAsync(session.RefreshToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteMyAccount_Success_EmailCanBeRegisteredAgain()
    {
        var dave = await CreateUserAsync("Dave");

        (await SendAsync(dave, new DeleteMyAccountCommand())).ShouldSucceed();

        (await SendAsync(null, new RegisterCommand("Dave 2", "dave@example.com", null, Password))).ShouldSucceed();
    }

    [Fact]
    public async Task DeleteMyAccount_Success_DeletedUserIsExcludedFromSearch()
    {
        var alice = await CreateUserAsync("Alice");
        var dave = await CreateUserAsync("Dave");
        (await SendAsync(dave, new DeleteMyAccountCommand())).ShouldSucceed();
        await RegisterAsync("Dave 2", "dave@example.com");

        var result = (await SendAsync(alice, new SearchUsersQuery("Dave"))).ShouldSucceed();

        result.ShouldHaveSingleItem().DisplayName.ShouldBe("Dave 2");
    }

    [Fact]
    public async Task DeleteMyAccount_OwnerOfSeveralGroups_Succeeds()
    {
        var scenario = await CreateOwenScenarioAsync();

        var result = await SendAsync(scenario.Owen, new DeleteMyAccountCommand());

        result.ShouldSucceed();
    }

    [Fact]
    public async Task DeleteMyAccount_OwnerWithMembers_TransfersOwnershipToHighestRoleThenEarliestJoin()
    {
        var s = await CreateOwenScenarioAsync();

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        var members = (await SendAsync(s.Mia, new GetConversationDetailsQuery(s.OwenTeam))).ShouldSucceed().Members;
        members.Count.ShouldBe(3);
        members.ShouldNotContain(m => m.User.Id == s.Owen);
        members.Single(m => m.User.Id == s.Max).Role.ShouldBe(GroupRole.Owner);
        members.Single(m => m.User.Id == s.Moe).Role.ShouldBe(GroupRole.Admin);
        members.Single(m => m.User.Id == s.Mia).Role.ShouldBe(GroupRole.Member);
    }

    [Fact]
    public async Task DeleteMyAccount_SuccessorsTiedOnRoleAndJoinTime_TransfersToTheLowestMemberId()
    {
        var s = await CreateOwenScenarioAsync();

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        var roles = (await SendAsync(s.Max, new GetConversationDetailsQuery(s.Tie))).ShouldSucceed()
            .Members.ToDictionary(m => m.User.Id, m => m.Role);
        roles.Count.ShouldBe(2);
        roles[s.TieWinner].ShouldBe(GroupRole.Owner);
        roles.Values.Count(r => r == GroupRole.Owner).ShouldBe(1);
    }

    [Fact]
    public async Task DeleteMyAccount_SoleOwner_JustLeavesTheGroup()
    {
        var s = await CreateOwenScenarioAsync();

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        var memberships = await OwenMembershipsAsync(s.Owen);
        memberships[s.Solo].IsActive.ShouldBeFalse();
        (await QueryDbAsync(db => db.ConversationMembers.CountAsync(m => m.ConversationId == s.Solo && m.IsActive))).ShouldBe(0);
    }

    [Fact]
    public async Task DeleteMyAccount_PlainMember_LeavesAndTheGroupOwnerIsUnchanged()
    {
        var s = await CreateOwenScenarioAsync();

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        (await OwenMembershipsAsync(s.Owen))[s.MiaGroup].IsActive.ShouldBeFalse();
        var details = (await SendAsync(s.Mia, new GetConversationDetailsQuery(s.MiaGroup))).ShouldSucceed();
        details.MyRole.ShouldBe(GroupRole.Owner);
        details.Members.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteMyAccount_DirectConversation_KeepsTheMembership()
    {
        var s = await CreateOwenScenarioAsync();

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        var memberships = await OwenMembershipsAsync(s.Owen);
        memberships[s.OwenTeam].IsActive.ShouldBeFalse();
        memberships[s.Tie].IsActive.ShouldBeFalse();
        memberships[s.Direct].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteMyAccount_MemberOfFourGroups_RaisesMemberLeftForEachGroup()
    {
        var s = await CreateOwenScenarioAsync();
        DomainEvents.Clear();

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        var left = DomainEvents.OfType<MemberLeftEvent>();
        left.Count.ShouldBe(4);
        left.ShouldAllBe(e => e.UserId == s.Owen);
        left.Select(e => e.ConversationId).ShouldBe([s.OwenTeam, s.Tie, s.Solo, s.MiaGroup], ignoreOrder: true);
    }

    [Fact]
    public async Task DeleteMyAccount_OwnerOfGroups_RaisesRoleChangesForBothSidesOfEachTransferOnly()
    {
        var s = await CreateOwenScenarioAsync();
        DomainEvents.Clear();

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        DomainEvents.OfType<MemberRoleChangedEvent>().ShouldBe(
        [
            new MemberRoleChangedEvent(s.OwenTeam, s.Owen, GroupRole.Admin),
            new MemberRoleChangedEvent(s.OwenTeam, s.Max, GroupRole.Owner),
            new MemberRoleChangedEvent(s.Tie, s.Owen, GroupRole.Admin),
            new MemberRoleChangedEvent(s.Tie, s.TieWinner, GroupRole.Owner)
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task DeleteMyAccount_MemberOfFourGroups_RemovedFromTheirSignalRGroupsButNotTheDirectOne()
    {
        var s = await CreateOwenScenarioAsync();
        var callsBefore = SignalR.GroupCalls.Count;

        (await SendAsync(s.Owen, new DeleteMyAccountCommand())).ShouldSucceed();

        SignalR.GroupCalls.Skip(callsBefore).ShouldBe(
            new[] { s.OwenTeam, s.Tie, s.Solo, s.MiaGroup }.Select(id => RecordingSignalRService.Removed(s.Owen, id)),
            ignoreOrder: true);
    }

    private Task<Dictionary<Guid, Domain.Entities.ConversationMember>> OwenMembershipsAsync(Guid owen) =>
        QueryDbAsync(db => db.ConversationMembers.AsNoTracking().Where(m => m.UserId == owen).ToDictionaryAsync(m => m.ConversationId));

    private async Task<OwenScenario> CreateOwenScenarioAsync()
    {
        var owen = await CreateUserAsync("Owen");
        var mia = await CreateUserAsync("Mia");
        var max = await CreateUserAsync("Max");
        var moe = await CreateUserAsync("Moe");

        var owenTeam = await CreateGroupAsync(owen, "Owen team", mia);
        await PauseAsync();
        await AddMemberAsync(owen, owenTeam, max);
        await PauseAsync();
        await AddMemberAsync(owen, owenTeam, moe);
        (await SendAsync(owen, new ChangeMemberRoleCommand(GroupRole.Admin) { ConversationId = owenTeam, UserId = moe })).ShouldSucceed();
        (await SendAsync(owen, new ChangeMemberRoleCommand(GroupRole.Admin) { ConversationId = owenTeam, UserId = max })).ShouldSucceed();

        var tie = await CreateGroupAsync(owen, "Tie", max, moe);
        var tieAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await UseDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE \"ConversationMembers\" SET \"JoinedAtUtc\" = {tieAt} WHERE \"ConversationId\" = {tie} AND \"UserId\" <> {owen}"));
        var tieWinner = (await QueryDbAsync(db => db.ConversationMembers.AsNoTracking()
                .Where(m => m.ConversationId == tie && m.UserId != owen).ToListAsync()))
            .OrderBy(m => m.Id).First().UserId;

        var solo = await CreateGroupAsync(owen, "Solo");
        var miaGroup = await CreateGroupAsync(mia, "Mia group", owen);
        var direct = await CreateDirectAsync(mia, owen);

        return new OwenScenario(owen, mia, max, moe, owenTeam, tie, tieWinner, solo, miaGroup, direct);
    }

    private sealed record OwenScenario(
        Guid Owen, Guid Mia, Guid Max, Guid Moe,
        Guid OwenTeam, Guid Tie, Guid TieWinner, Guid Solo, Guid MiaGroup, Guid Direct);
}
