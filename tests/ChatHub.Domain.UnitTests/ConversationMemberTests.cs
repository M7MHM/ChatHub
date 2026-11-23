using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class ConversationMemberTests
{
    private readonly Guid _conversationId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();

    [Fact]
    public void Create_ValidIds_CreatesAnActiveUnmutedMember()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        member.IsActive.ShouldBeTrue();
        member.Role.ShouldBe(GroupRole.Member);
        member.IsMuted.ShouldBeFalse();
        member.JoinedAtUtc.ShouldBeInRange(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Create_EmptyId_Throws(bool emptyConversation, bool emptyUser)
    {
        Should.Throw<DomainRuleViolationException>(() => ConversationMember.Create(
            emptyConversation ? Guid.Empty : _conversationId, emptyUser ? Guid.Empty : _userId));
    }

    [Fact]
    public void Create_UndefinedRole_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => ConversationMember.Create(_conversationId, _userId, (GroupRole)42));
    }

    [Fact]
    public void ChangeRole_UndefinedRole_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        Should.Throw<DomainRuleViolationException>(() => member.ChangeRole((GroupRole)42));
    }

    [Fact]
    public void ChangeRole_InactiveMember_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        member.Leave();

        Should.Throw<DomainRuleViolationException>(() => member.ChangeRole(GroupRole.Admin));
    }

    [Fact]
    public void Leave_ActiveMember_DeactivatesAndRecordsTheTime()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        member.Leave();

        member.IsActive.ShouldBeFalse();
        member.LeftAtUtc.ShouldNotBeNull();
        member.KickedByUserId.ShouldBeNull();
    }

    [Fact]
    public void Leave_InactiveMember_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        member.Leave();

        Should.Throw<DomainRuleViolationException>(member.Leave);
    }

    [Fact]
    public void Kick_ByAnotherUser_DeactivatesAndRecordsWhoKicked()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        member.Kick(_otherUserId);

        member.IsActive.ShouldBeFalse();
        member.KickedByUserId.ShouldBe(_otherUserId);
    }

    [Fact]
    public void Kick_EmptyKickerId_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        Should.Throw<DomainRuleViolationException>(() => member.Kick(Guid.Empty));
    }

    [Fact]
    public void Kick_ByThemselves_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        Should.Throw<DomainRuleViolationException>(() => member.Kick(_userId));
    }

    [Fact]
    public void Kick_Owner_Throws()
    {
        var owner = ConversationMember.Create(_conversationId, _userId, GroupRole.Owner);

        Should.Throw<DomainRuleViolationException>(() => owner.Kick(_otherUserId));
    }

    [Fact]
    public void Rejoin_KickedMember_ReactivatesAndResetsTheLeaveState()
    {
        var member = ConversationMember.Create(_conversationId, _userId, GroupRole.Admin);
        member.Kick(_otherUserId);

        member.Rejoin();

        member.IsActive.ShouldBeTrue();
        member.Role.ShouldBe(GroupRole.Member);
        member.LeftAtUtc.ShouldBeNull();
        member.KickedByUserId.ShouldBeNull();
    }

    [Fact]
    public void Rejoin_ActiveMember_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        Should.Throw<DomainRuleViolationException>(() => member.Rejoin());
    }

    [Fact]
    public void ClearChat_ActiveMember_RecordsTheClearTime()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        member.ClearChat();

        member.ClearedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public void MuteThenUnmute_ActiveMember_TogglesMuted()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        member.Mute();
        var mutedAfterMute = member.IsMuted;
        member.Unmute();

        mutedAfterMute.ShouldBeTrue();
        member.IsMuted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(nameof(ConversationMember.ClearChat))]
    [InlineData(nameof(ConversationMember.Mute))]
    [InlineData(nameof(ConversationMember.Unmute))]
    public void ChatSetting_InactiveMember_Throws(string action)
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        member.Leave();

        Should.Throw<DomainRuleViolationException>(() =>
        {
            switch (action)
            {
                case nameof(ConversationMember.ClearChat): member.ClearChat(); break;
                case nameof(ConversationMember.Mute): member.Mute(); break;
                default: member.Unmute(); break;
            }
        });
    }

    [Fact]
    public void MarkAsRead_OwnMessage_MovesNoPointerAndRaisesNoEvent()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var own = TestData.TextAfter(null, _conversationId, _userId);

        member.MarkAsRead(own);

        member.LastReadMessageId.ShouldBeNull();
        member.LastDeliveredMessageId.ShouldBeNull();
        member.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void MarkAsDelivered_OwnMessage_MovesNoPointerAndRaisesNoEvent()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var own = TestData.TextAfter(null, _conversationId, _userId);

        member.MarkAsDelivered(own);

        member.LastDeliveredMessageId.ShouldBeNull();
        member.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void MarkAsRead_NewerMessage_AdvancesBothPointersAndRaisesDeliveredAndRead()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var message = TestData.TextAfter(null, _conversationId, _otherUserId);

        member.MarkAsRead(message);

        member.LastReadMessageId.ShouldBe(message.Id);
        member.LastReadMessageAtUtc.ShouldBe(message.CreatedAtUtc);
        member.LastDeliveredMessageId.ShouldBe(message.Id);
        member.DomainEvents.Count.ShouldBe(2);
        member.DomainEvents.First().ShouldBe(new MessageDeliveredEvent(_conversationId, _userId, message.Id));
        var read = member.DomainEvents.Last().ShouldBeOfType<MessageReadEvent>();
        read.MessageId.ShouldBe(message.Id);
        read.UserId.ShouldBe(_userId);
        read.ReadUpToUtc.ShouldBe(message.CreatedAtUtc);
    }

    [Fact]
    public void MarkAsRead_OlderThanTheReadPointer_KeepsPointersAndRaisesNoEvent()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var older = TestData.TextAfter(null, _conversationId, _otherUserId);
        var newer = TestData.TextAfter(older, _conversationId, _otherUserId);
        member.MarkAsRead(newer);
        member.ClearDomainEvents();

        member.MarkAsRead(older);

        member.LastReadMessageId.ShouldBe(newer.Id);
        member.LastDeliveredMessageId.ShouldBe(newer.Id);
        member.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void MarkAsRead_AlreadyDeliveredMessage_RaisesOnlyMessageRead()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var message = TestData.TextAfter(null, _conversationId, _otherUserId);
        member.MarkAsDelivered(message);
        member.ClearDomainEvents();

        member.MarkAsRead(message);

        member.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<MessageReadEvent>();
    }

    [Fact]
    public void MarkAsDelivered_NewerMessage_AdvancesOnlyTheDeliveredPointer()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var message = TestData.TextAfter(null, _conversationId, _otherUserId);

        member.MarkAsDelivered(message);

        member.LastDeliveredMessageId.ShouldBe(message.Id);
        member.LastReadMessageId.ShouldBeNull();
        member.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MessageDeliveredEvent(_conversationId, _userId, message.Id));
    }

    [Fact]
    public void MarkAsDelivered_SameMessageTwice_RaisesOneEvent()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var message = TestData.TextAfter(null, _conversationId, _otherUserId);

        member.MarkAsDelivered(message);
        member.MarkAsDelivered(message);

        member.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void MarkAsRead_MessageFromAnotherConversation_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        var foreign = TestData.TextAfter(null, Guid.NewGuid(), _otherUserId);

        Should.Throw<DomainRuleViolationException>(() => member.MarkAsRead(foreign));
    }

    [Fact]
    public void MarkAsDelivered_InactiveMember_Throws()
    {
        var member = ConversationMember.Create(_conversationId, _userId);
        member.Leave();
        var message = TestData.TextAfter(null, _conversationId, _otherUserId);

        Should.Throw<DomainRuleViolationException>(() => member.MarkAsDelivered(message));
    }

    [Fact]
    public void MarkAsRead_NullMessage_ThrowsArgumentNullException()
    {
        var member = ConversationMember.Create(_conversationId, _userId);

        Should.Throw<ArgumentNullException>(() => member.MarkAsRead(null!));
    }

    [Fact]
    public void MuteAndUnmute_RaiseMemberMuteChangedOnlyWhenTheStateChanges()
    {
        var member = ConversationMember.Create(Guid.NewGuid(), Guid.NewGuid());

        member.Mute();
        member.Mute();
        member.Unmute();
        member.Unmute();

        member.DomainEvents.ShouldBe(
        [
            new MemberMuteChangedEvent(member.ConversationId, member.UserId, true),
            new MemberMuteChangedEvent(member.ConversationId, member.UserId, false)
        ]);
    }
}
