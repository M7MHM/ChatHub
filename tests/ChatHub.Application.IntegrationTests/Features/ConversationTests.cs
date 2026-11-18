using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class ConversationTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task CreateDirectConversation_RepeatedFromEitherSide_ReusesTheSameConversation()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");

        var first = (await SendAsync(alice, new CreateDirectConversationCommand(bob))).ShouldSucceed();
        var again = (await SendAsync(alice, new CreateDirectConversationCommand(bob))).ShouldSucceed();
        var otherSide = (await SendAsync(bob, new CreateDirectConversationCommand(alice))).ShouldSucceed();

        first.Created.ShouldBeTrue();
        again.Created.ShouldBeFalse();
        otherSide.Created.ShouldBeFalse();
        again.ConversationId.ShouldBe(first.ConversationId);
        otherSide.ConversationId.ShouldBe(first.ConversationId);
    }

    [Fact]
    public async Task CreateDirectConversation_New_AddsBothUsersToTheSignalRGroup()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");

        var direct = await CreateDirectAsync(alice, bob);
        await CreateDirectAsync(alice, bob);

        SignalR.GroupCalls.ShouldBe([RecordingSignalRService.Added(alice, direct), RecordingSignalRService.Added(bob, direct)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task CreateDirectConversation_WithUserWhoBlockedTheCaller_ReturnsBlocked()
    {
        var carol = await CreateUserAsync("Carol");
        var dave = await CreateUserAsync("Dave");
        await BlockAsync(carol, dave);

        var result = await SendAsync(dave, new CreateDirectConversationCommand(carol));

        result.ShouldFailWith(ConversationErrors.Blocked);
    }

    [Fact]
    public async Task CreateDirectConversation_WithSelf_ReturnsCannotChatWithSelf()
    {
        var alice = await CreateUserAsync("Alice");

        var result = await SendAsync(alice, new CreateDirectConversationCommand(alice));

        result.ShouldFailWith(ConversationErrors.CannotChatWithSelf);
    }

    [Fact]
    public async Task CreateGroup_MemberListWithDuplicatesUnknownAndOwner_KeepsOwnerAndExistingUsersOnce()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();

        var group = (await SendAsync(alice, new CreateGroupCommand("Team", null, [bob, carol, dave, Guid.NewGuid(), alice, bob])))
            .ShouldSucceed();

        group.MemberIds.ShouldBe([alice, bob, carol, dave], ignoreOrder: true);
    }

    [Fact]
    public async Task GetConversationDetails_GroupOwner_ReturnsMyRoleAndMembersOwnerFirst()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        var details = (await SendAsync(alice, new GetConversationDetailsQuery(group))).ShouldSucceed();

        details.MyRole.ShouldBe(GroupRole.Owner);
        details.Members.Count.ShouldBe(4);
        details.Members[0].Role.ShouldBe(GroupRole.Owner);
    }

    [Fact]
    public async Task GetConversationDetails_Direct_TakesTitleAndOtherUserFromTheOtherMember()
    {
        var alice = await CreateUserAsync("Alice A.", "alice@example.com");
        var bob = await CreateUserAsync("Bob");
        var direct = await CreateDirectAsync(alice, bob);

        var details = (await SendAsync(bob, new GetConversationDetailsQuery(direct))).ShouldSucceed();

        details.Title.ShouldBe("Alice A.");
        details.OtherUser.ShouldNotBeNull().Id.ShouldBe(alice);
    }

    [Fact]
    public async Task GetConversationDetails_NonMember_ReturnsNotMember()
    {
        var (alice, bob, carol, _) = await CreateFourUsersAsync();
        var direct = await CreateDirectAsync(alice, bob);

        var result = await SendAsync(carol, new GetConversationDetailsQuery(direct));

        result.ShouldFailWith(ConversationErrors.NotMember);
    }

    [Fact]
    public async Task GetConversationDetails_UnknownConversation_ReturnsNotFound()
    {
        var carol = await CreateUserAsync("Carol");

        var result = await SendAsync(carol, new GetConversationDetailsQuery(Guid.NewGuid()));

        result.ShouldFailWith(ConversationErrors.NotFound);
    }

    [Fact]
    public async Task UpdateGroupInfo_ByOwner_Succeeds()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        var result = await SendAsync(alice, new UpdateGroupInfoCommand("Team 2") { ConversationId = group });

        result.ShouldSucceed();
    }

    [Fact]
    public async Task UpdateGroupInfo_ByPlainMember_ThrowsDomainRuleViolation()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        await Should.ThrowAsync<DomainRuleViolationException>(() =>
            SendAsync(bob, new UpdateGroupInfoCommand("x") { ConversationId = group }));
    }

    [Fact]
    public async Task ChangeMemberRole_OwnerPromotesMemberToAdmin_Succeeds()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        var result = await SendAsync(alice, new ChangeMemberRoleCommand(GroupRole.Admin) { ConversationId = group, UserId = bob });

        result.ShouldSucceed();
    }

    [Fact]
    public async Task ChangeMemberRole_ToOwner_ThrowsValidationException()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        await Should.ThrowAsync<ValidationException>(() =>
            SendAsync(alice, new ChangeMemberRoleCommand(GroupRole.Owner) { ConversationId = group, UserId = carol }));
    }

    [Fact]
    public async Task KickMember_AdminKicksPlainMember_Succeeds()
    {
        var (_, bob, _, dave, group) = await CreateTeamWithBobAsAdminAsync();

        var result = await SendAsync(bob, new KickMemberCommand(group, dave));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task KickMember_Success_RemovesTheKickedUserFromTheSignalRGroup()
    {
        var (_, bob, _, dave, group) = await CreateTeamWithBobAsAdminAsync();

        (await SendAsync(bob, new KickMemberCommand(group, dave))).ShouldSucceed();

        SignalR.GroupCalls.ShouldContain(RecordingSignalRService.Removed(dave, group));
    }

    [Fact]
    public async Task KickMember_Success_KickedUserLosesAccess()
    {
        var (_, bob, _, dave, group) = await CreateTeamWithBobAsAdminAsync();

        (await SendAsync(bob, new KickMemberCommand(group, dave))).ShouldSucceed();

        (await SendAsync(dave, new GetConversationDetailsQuery(group))).ShouldFailWith(ConversationErrors.NotMember);
    }

    [Fact]
    public async Task AddMember_PreviouslyKickedUser_RejoinsAndJoinsTheSignalRGroup()
    {
        var (alice, bob, _, dave, group) = await CreateTeamWithBobAsAdminAsync();
        (await SendAsync(bob, new KickMemberCommand(group, dave))).ShouldSucceed();
        var callsBefore = SignalR.GroupCalls.Count;

        var result = await SendAsync(alice, new AddMemberCommand(dave) { ConversationId = group });

        result.ShouldSucceed();
        SignalR.GroupCalls.Skip(callsBefore).ShouldBe([RecordingSignalRService.Added(dave, group)]);
        (await SendAsync(dave, new GetConversationDetailsQuery(group))).ShouldSucceed();
    }

    [Fact]
    public async Task AddMember_NewUserToGroup_Succeeds()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Crew", bob, carol);

        var result = await SendAsync(alice, new AddMemberCommand(dave) { ConversationId = group });

        result.ShouldSucceed();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddMember_UserBlockedEitherWay_ReturnsBlocked(bool actorIsTheBlocker)
    {
        var (alice, bob, carol, _) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob);
        if (actorIsTheBlocker) await BlockAsync(alice, carol);
        else await BlockAsync(carol, alice);

        var result = await SendAsync(alice, new AddMemberCommand(carol) { ConversationId = group });

        result.ShouldFailWith(ConversationErrors.Blocked);
    }

    [Fact]
    public async Task AddMember_UserWhoLeft_CanBeAddedAgain()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);
        (await SendAsync(carol, new LeaveConversationCommand(group))).ShouldSucceed();

        var result = await SendAsync(alice, new AddMemberCommand(carol) { ConversationId = group });

        result.ShouldSucceed();
    }

    [Fact]
    public async Task LeaveConversation_GroupMember_LeavesAndIsRemovedFromTheSignalRGroup()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        var result = await SendAsync(carol, new LeaveConversationCommand(group));

        result.ShouldSucceed();
        SignalR.GroupCalls.ShouldContain(RecordingSignalRService.Removed(carol, group));
    }

    [Fact]
    public async Task LeaveConversation_Direct_ThrowsDomainRuleViolation()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        var direct = await CreateDirectAsync(alice, bob);

        await Should.ThrowAsync<DomainRuleViolationException>(() => SendAsync(alice, new LeaveConversationCommand(direct)));
    }

    [Fact]
    public async Task LeaveConversation_OwnerWithOtherMembers_ThrowsDomainRuleViolation()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        await Should.ThrowAsync<DomainRuleViolationException>(() => SendAsync(alice, new LeaveConversationCommand(group)));
    }

    [Fact]
    public async Task TransferOwnership_ToMember_Succeeds()
    {
        var (alice, bob, _, _, group) = await CreateTeamWithBobAsAdminAsync();

        var result = await SendAsync(alice, new TransferOwnershipCommand(bob) { ConversationId = group });

        result.ShouldSucceed();
    }

    [Fact]
    public async Task TransferOwnership_Success_FormerOwnerBecomesAdminAndNewOwnerIsOwner()
    {
        var (alice, bob, _, _, group) = await CreateTeamWithBobAsAdminAsync();

        (await SendAsync(alice, new TransferOwnershipCommand(bob) { ConversationId = group })).ShouldSucceed();

        var details = (await SendAsync(alice, new GetConversationDetailsQuery(group))).ShouldSucceed();
        details.MyRole.ShouldBe(GroupRole.Admin);
        details.Members.Single(m => m.User.Id == bob).Role.ShouldBe(GroupRole.Owner);
    }

    [Fact]
    public async Task MuteConversation_Member_Succeeds()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        var result = await SendAsync(alice, new MuteConversationCommand(group));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task MuteConversation_Success_ShowsAsMutedInTheList()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);

        (await SendAsync(alice, new MuteConversationCommand(group))).ShouldSucceed();

        var list = (await SendAsync(alice, new GetMyConversationsQuery())).ShouldSucceed();
        list.Items.Single(i => i.Id == group).IsMuted.ShouldBeTrue();
    }

    [Fact]
    public async Task UnmuteConversation_MutedMember_Succeeds()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);
        (await SendAsync(alice, new MuteConversationCommand(group))).ShouldSucceed();

        var result = await SendAsync(alice, new UnmuteConversationCommand(group));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task MuteConversation_NonMember_ReturnsNotMember()
    {
        var (alice, bob, carol, _) = await CreateFourUsersAsync();
        var direct = await CreateDirectAsync(alice, bob);

        var result = await SendAsync(carol, new MuteConversationCommand(direct));

        result.ShouldFailWith(ConversationErrors.NotMember);
    }

    [Fact]
    public async Task MuteConversation_UnknownConversation_ReturnsNotFound()
    {
        var carol = await CreateUserAsync("Carol");

        var result = await SendAsync(carol, new MuteConversationCommand(Guid.NewGuid()));

        result.ShouldFailWith(ConversationErrors.NotFound);
    }

    [Fact]
    public async Task ClearChat_Member_Succeeds()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);
        await SendTextAsync(bob, group, "hello team");

        var result = await SendAsync(alice, new ClearChatCommand(group));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task ClearChat_AfterMuteAndUnmute_ResetsUnreadAndPreviewAndStaysUnmuted()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);
        await SendTextAsync(bob, group, "hello team");
        await SendTextAsync(carol, group, "hi");
        (await SendAsync(alice, new MuteConversationCommand(group))).ShouldSucceed();
        (await SendAsync(alice, new UnmuteConversationCommand(group))).ShouldSucceed();
        await PauseAsync();

        (await SendAsync(alice, new ClearChatCommand(group))).ShouldSucceed();

        var item = (await SendAsync(alice, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Id == group);
        item.UnreadCount.ShouldBe(0);
        item.LastMessage.ShouldBeNull();
        item.IsMuted.ShouldBeFalse();
    }

    private async Task<(Guid Alice, Guid Bob, Guid Carol, Guid Dave)> CreateFourUsersAsync() =>
        (await CreateUserAsync("Alice"), await CreateUserAsync("Bob"), await CreateUserAsync("Carol"), await CreateUserAsync("Dave"));

    private async Task<(Guid Alice, Guid Bob, Guid Carol, Guid Dave, Guid Group)> CreateTeamWithBobAsAdminAsync()
    {
        var (alice, bob, carol, dave) = await CreateFourUsersAsync();
        var group = await CreateGroupAsync(alice, "Team", bob, carol, dave);
        (await SendAsync(alice, new ChangeMemberRoleCommand(GroupRole.Admin) { ConversationId = group, UserId = bob })).ShouldSucceed();
        return (alice, bob, carol, dave, group);
    }
}
