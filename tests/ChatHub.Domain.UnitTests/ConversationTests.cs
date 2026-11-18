using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class ConversationTests
{
    [Fact]
    public void CreateDirect_TwoUsers_CreatesADirectConversationWithBothMembersAndItsKey()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var direct = Conversation.CreateDirect(alice, bob);

        direct.Type.ShouldBe(ConversationType.Direct);
        direct.Title.ShouldBeNull();
        direct.DirectKey.ShouldBe(Conversation.BuildDirectKey(alice, bob));
        direct.Members.Select(m => m.UserId).ShouldBe([alice, bob], ignoreOrder: true);
        direct.Members.ShouldAllBe(m => m.IsActive && m.Role == GroupRole.Member && m.ConversationId == direct.Id);
    }

    [Fact]
    public void CreateDirect_TwoUsers_RaisesConversationCreatedWithBothMembers()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var direct = Conversation.CreateDirect(alice, bob);

        var created = direct.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ConversationCreatedEvent>();
        created.ConversationId.ShouldBe(direct.Id);
        created.Type.ShouldBe(ConversationType.Direct);
        created.MemberIds.ShouldBe([alice, bob], ignoreOrder: true);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CreateDirect_EmptyUserId_Throws(bool firstEmpty, bool secondEmpty)
    {
        var first = firstEmpty ? Guid.Empty : Guid.NewGuid();
        var second = secondEmpty ? Guid.Empty : Guid.NewGuid();

        Should.Throw<DomainRuleViolationException>(() => Conversation.CreateDirect(first, second));
    }

    [Fact]
    public void CreateDirect_SameUserTwice_Throws()
    {
        var alice = Guid.NewGuid();

        Should.Throw<DomainRuleViolationException>(() => Conversation.CreateDirect(alice, alice));
    }

    [Fact]
    public void BuildDirectKey_UsersInEitherOrder_ReturnsTheSameKey()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        Conversation.BuildDirectKey(alice, bob).ShouldBe(Conversation.BuildDirectKey(bob, alice));
    }

    [Fact]
    public void CreateGroup_ValidTitle_CreatesAGroupOwnedByTheCreatorWithTrimmedValues()
    {
        var owner = Guid.NewGuid();

        var group = Conversation.CreateGroup(owner, "  Team  ", "  /api/files/x  ");

        group.Type.ShouldBe(ConversationType.Group);
        group.Title.ShouldBe("Team");
        group.ImageUrl.ShouldBe("/api/files/x");
        group.DirectKey.ShouldBeNull();
        group.Members.ShouldHaveSingleItem().Role.ShouldBe(GroupRole.Owner);
    }

    [Fact]
    public void CreateGroup_Valid_RaisesConversationCreatedWithTheOwnerOnly()
    {
        var owner = Guid.NewGuid();

        var group = Conversation.CreateGroup(owner, "Team");

        group.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ConversationCreatedEvent>().MemberIds.ShouldBe([owner]);
    }

    [Fact]
    public void CreateGroup_WhitespaceImageUrl_StoresNoImage()
    {
        var group = Conversation.CreateGroup(Guid.NewGuid(), "Team", "   ");

        group.ImageUrl.ShouldBeNull();
    }

    [Fact]
    public void CreateGroup_EmptyOwnerId_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => Conversation.CreateGroup(Guid.Empty, "Team"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateGroup_BlankTitle_Throws(string title)
    {
        Should.Throw<DomainRuleViolationException>(() => Conversation.CreateGroup(Guid.NewGuid(), title));
    }

    [Fact]
    public void CreateGroup_TitleLongerThanTheMaximum_Throws()
    {
        var title = new string('x', Conversation.MaxTitleLength + 1);

        Should.Throw<DomainRuleViolationException>(() => Conversation.CreateGroup(Guid.NewGuid(), title));
    }

    [Fact]
    public void UpdateGroupInfo_ByAdmin_UpdatesAndRaisesGroupInfoUpdated()
    {
        var (group, _, admin, _) = TestData.GroupWithAdminAndMember();

        group.UpdateGroupInfo(admin, " Team 2 ", "/api/files/new");

        group.Title.ShouldBe("Team 2");
        group.ImageUrl.ShouldBe("/api/files/new");
        group.DomainEvents.ShouldHaveSingleItem().ShouldBe(new GroupInfoUpdatedEvent(group.Id, admin));
    }

    [Fact]
    public void UpdateGroupInfo_SameValues_RaisesNoEvent()
    {
        var (group, owner, _) = TestData.GroupWithMember();

        group.UpdateGroupInfo(owner, "Team", null);

        group.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void UpdateGroupInfo_ByPlainMember_Throws()
    {
        var (group, _, member) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.UpdateGroupInfo(member, "x", null));
    }

    [Fact]
    public void UpdateGroupInfo_OnADirectConversation_Throws()
    {
        var alice = Guid.NewGuid();
        var direct = Conversation.CreateDirect(alice, Guid.NewGuid());

        Should.Throw<DomainRuleViolationException>(() => direct.UpdateGroupInfo(alice, "x", null));
    }

    [Fact]
    public void AddMember_NewUser_AddsAnActiveMemberAndRaisesMemberAdded()
    {
        var (group, owner, _) = TestData.GroupWithMember();
        var carol = Guid.NewGuid();

        var member = group.AddMember(owner, carol);

        member.IsActive.ShouldBeTrue();
        member.Role.ShouldBe(GroupRole.Member);
        group.FindActiveMember(carol).ShouldBe(member);
        group.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MemberAddedEvent(group.Id, carol, owner));
    }

    [Fact]
    public void AddMember_AlreadyActiveMember_Throws()
    {
        var (group, owner, member) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.AddMember(owner, member));
    }

    [Fact]
    public void AddMember_FormerMember_ReactivatesTheExistingMembership()
    {
        var (group, owner, member) = TestData.GroupWithMember();
        var membership = group.FindMember(member)!;
        group.LeaveMember(member);

        var readded = group.AddMember(owner, member);

        readded.ShouldBeSameAs(membership);
        readded.IsActive.ShouldBeTrue();
        group.Members.Count.ShouldBe(2);
    }

    [Fact]
    public void AddMember_ByPlainMember_Throws()
    {
        var (group, _, member) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.AddMember(member, Guid.NewGuid()));
    }

    [Fact]
    public void AddMember_ToADirectConversation_Throws()
    {
        var alice = Guid.NewGuid();
        var direct = Conversation.CreateDirect(alice, Guid.NewGuid());

        Should.Throw<DomainRuleViolationException>(() => direct.AddMember(alice, Guid.NewGuid()));
    }

    [Fact]
    public void KickMember_AdminKicksMember_DeactivatesAndRaisesMemberKicked()
    {
        var (group, _, admin, member) = TestData.GroupWithAdminAndMember();

        group.KickMember(admin, member);

        var membership = group.FindMember(member)!;
        membership.IsActive.ShouldBeFalse();
        membership.KickedByUserId.ShouldBe(admin);
        group.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MemberKickedEvent(group.Id, member, admin));
    }

    [Fact]
    public void KickMember_TargetWithTheSameRole_Throws()
    {
        var (group, owner, admin, member) = TestData.GroupWithAdminAndMember();
        group.ChangeMemberRole(owner, member, GroupRole.Admin);

        Should.Throw<DomainRuleViolationException>(() => group.KickMember(admin, member));
    }

    [Fact]
    public void KickMember_TheOwner_Throws()
    {
        var (group, owner, admin, _) = TestData.GroupWithAdminAndMember();

        Should.Throw<DomainRuleViolationException>(() => group.KickMember(admin, owner));
    }

    [Fact]
    public void KickMember_InactiveTarget_Throws()
    {
        var (group, owner, member) = TestData.GroupWithMember();
        group.LeaveMember(member);

        Should.Throw<DomainRuleViolationException>(() => group.KickMember(owner, member));
    }

    [Fact]
    public void KickMember_ByPlainMember_Throws()
    {
        var (group, owner, _, member) = TestData.GroupWithAdminAndMember();
        var other = Guid.NewGuid();
        group.AddMember(owner, other);

        Should.Throw<DomainRuleViolationException>(() => group.KickMember(member, other));
    }

    [Fact]
    public void LeaveMember_PlainMember_DeactivatesAndRaisesMemberLeft()
    {
        var (group, _, member) = TestData.GroupWithMember();

        group.LeaveMember(member);

        group.FindActiveMember(member).ShouldBeNull();
        group.FindMember(member)!.LeftAtUtc.ShouldNotBeNull();
        group.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MemberLeftEvent(group.Id, member));
    }

    [Fact]
    public void LeaveMember_DirectConversation_Throws()
    {
        var alice = Guid.NewGuid();
        var direct = Conversation.CreateDirect(alice, Guid.NewGuid());

        var exception = Should.Throw<DomainRuleViolationException>(() => direct.LeaveMember(alice));

        exception.Message.ShouldBe("Direct conversations cannot be left; clear the chat instead.");
    }

    [Fact]
    public void LeaveMember_OwnerWithOtherActiveMembers_Throws()
    {
        var (group, owner, _) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.LeaveMember(owner));
    }

    [Fact]
    public void LeaveMember_SoleOwner_Leaves()
    {
        var owner = Guid.NewGuid();
        var group = Conversation.CreateGroup(owner, "Solo");

        group.LeaveMember(owner);

        group.Members.ShouldAllBe(m => !m.IsActive);
    }

    [Fact]
    public void LeaveMember_UserWhoIsNotActive_Throws()
    {
        var (group, _, _) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.LeaveMember(Guid.NewGuid()));
    }

    [Fact]
    public void ChangeMemberRole_OwnerPromotesMember_ChangesTheRoleAndRaisesMemberRoleChanged()
    {
        var (group, owner, member) = TestData.GroupWithMember();

        group.ChangeMemberRole(owner, member, GroupRole.Admin);

        group.FindActiveMember(member)!.Role.ShouldBe(GroupRole.Admin);
        group.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MemberRoleChangedEvent(group.Id, member, GroupRole.Admin));
    }

    [Fact]
    public void ChangeMemberRole_SameRole_RaisesNoEvent()
    {
        var (group, owner, member) = TestData.GroupWithMember();

        group.ChangeMemberRole(owner, member, GroupRole.Member);

        group.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void ChangeMemberRole_ToOwner_Throws()
    {
        var (group, owner, member) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.ChangeMemberRole(owner, member, GroupRole.Owner));
    }

    [Fact]
    public void ChangeMemberRole_ByAdmin_Throws()
    {
        var (group, _, admin, member) = TestData.GroupWithAdminAndMember();

        Should.Throw<DomainRuleViolationException>(() => group.ChangeMemberRole(admin, member, GroupRole.Admin));
    }

    [Fact]
    public void ChangeMemberRole_OfTheOwner_Throws()
    {
        var (group, owner, _) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.ChangeMemberRole(owner, owner, GroupRole.Admin));
    }

    [Fact]
    public void ChangeMemberRole_InactiveTarget_Throws()
    {
        var (group, owner, member) = TestData.GroupWithMember();
        group.LeaveMember(member);

        Should.Throw<DomainRuleViolationException>(() => group.ChangeMemberRole(owner, member, GroupRole.Admin));
    }

    [Fact]
    public void TransferOwnership_ToActiveMember_SwapsRolesAndRaisesBothRoleChanges()
    {
        var (group, owner, member) = TestData.GroupWithMember();

        group.TransferOwnership(owner, member);

        group.FindActiveMember(owner)!.Role.ShouldBe(GroupRole.Admin);
        group.FindActiveMember(member)!.Role.ShouldBe(GroupRole.Owner);
        group.DomainEvents.ShouldBe(
        [
            new MemberRoleChangedEvent(group.Id, owner, GroupRole.Admin),
            new MemberRoleChangedEvent(group.Id, member, GroupRole.Owner)
        ]);
    }

    [Fact]
    public void TransferOwnership_ToTheCurrentOwner_Throws()
    {
        var (group, owner, _) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.TransferOwnership(owner, owner));
    }

    [Fact]
    public void TransferOwnership_ToAUserWhoIsNotActive_Throws()
    {
        var (group, owner, member) = TestData.GroupWithMember();
        group.LeaveMember(member);

        Should.Throw<DomainRuleViolationException>(() => group.TransferOwnership(owner, member));
    }

    [Fact]
    public void TransferOwnership_ByNonOwner_Throws()
    {
        var (group, owner, admin, _) = TestData.GroupWithAdminAndMember();

        Should.Throw<DomainRuleViolationException>(() => group.TransferOwnership(admin, owner));
    }

    [Fact]
    public void SendMessage_ActiveMember_CreatesTheMessageAndUpdatesLastMessage()
    {
        var (group, _, member) = TestData.GroupWithMember();

        var message = group.SendMessage(member, MessageType.Text, "hello");

        message.ConversationId.ShouldBe(group.Id);
        message.SenderId.ShouldBe(member);
        group.LastMessageId.ShouldBe(message.Id);
        group.LastMessageAtUtc.ShouldBe(message.CreatedAtUtc);
    }

    [Fact]
    public void SendMessage_NonMember_Throws()
    {
        var (group, _, _) = TestData.GroupWithMember();

        Should.Throw<DomainRuleViolationException>(() => group.SendMessage(Guid.NewGuid(), MessageType.Text, "hello"));
    }

    [Fact]
    public void SendMessage_FormerMember_Throws()
    {
        var (group, _, member) = TestData.GroupWithMember();
        group.LeaveMember(member);

        Should.Throw<DomainRuleViolationException>(() => group.SendMessage(member, MessageType.Text, "hello"));
    }

    [Fact]
    public void SendMessage_SystemMessageFromANonMember_IsAllowed()
    {
        var (group, _, _) = TestData.GroupWithMember();

        var message = group.SendMessage(Guid.NewGuid(), MessageType.System, "Group created");

        message.Type.ShouldBe(MessageType.System);
    }

    [Fact]
    public void SendMessage_ReplyToAMessageFromAnotherConversation_Throws()
    {
        var (group, owner, _) = TestData.GroupWithMember();
        var other = Conversation.CreateGroup(owner, "Other");
        var foreign = other.SendMessage(owner, MessageType.Text, "elsewhere");

        Should.Throw<DomainRuleViolationException>(() => group.SendMessage(owner, MessageType.Text, "re", replyTo: foreign));
    }

    [Fact]
    public void SendMessage_ReplyInTheSameConversation_SetsTheParentMessageId()
    {
        var (group, owner, member) = TestData.GroupWithMember();
        var parent = group.SendMessage(owner, MessageType.Text, "question");

        var reply = group.SendMessage(member, MessageType.Text, "answer", replyTo: parent);

        reply.ParentMessageId.ShouldBe(parent.Id);
    }
}
