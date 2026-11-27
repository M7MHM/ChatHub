using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class ConversationHistoryTests
{
    private static readonly DateTime Start = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BackdateHistory_MovesTheStartAndEveryJoinAndDropsTheCreationEvents()
    {
        var (owner, member) = (Guid.NewGuid(), Guid.NewGuid());
        var group = Conversation.CreateGroup(owner, "Trip");
        group.AddMember(owner, member);

        group.BackdateHistory(Start);

        group.CreatedAtUtc.ShouldBe(Start);
        group.Members.ShouldAllBe(m => m.JoinedAtUtc == Start && m.CreatedAtUtc == Start);
        group.DomainEvents.ShouldBeEmpty();
        group.Members.ShouldAllBe(m => m.DomainEvents.Count == 0);
    }

    [Fact]
    public void AddHistoryMessage_IsATextMessageAtTheGivenTimeWithoutEvents()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var direct = Conversation.CreateDirect(a, b);
        direct.BackdateHistory(Start);

        var first = direct.AddHistoryMessage(a, "  hello  ", Start.AddMinutes(1));
        var second = direct.AddHistoryMessage(b, "hi!", Start.AddMinutes(2));

        (first.Type, first.Content, first.CreatedAtUtc, first.SenderId).ShouldBe((MessageType.Text, "hello", Start.AddMinutes(1), a));
        first.DomainEvents.ShouldBeEmpty();
        (direct.LastMessageId, direct.LastMessageAtUtc).ShouldBe((second.Id, Start.AddMinutes(2)));
        direct.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void AddHistoryMessage_OutOfOrderBeforeTheStartOrFromANonMember_Throws()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var direct = Conversation.CreateDirect(a, b);
        direct.BackdateHistory(Start);
        direct.AddHistoryMessage(a, "later", Start.AddMinutes(10));

        Should.Throw<DomainRuleViolationException>(() => direct.AddHistoryMessage(a, "earlier", Start.AddMinutes(5)));
        Should.Throw<DomainRuleViolationException>(() => direct.AddHistoryMessage(a, "too early", Start.AddMinutes(-1)));
        Should.Throw<DomainRuleViolationException>(() => direct.AddHistoryMessage(Guid.NewGuid(), "stranger", Start.AddMinutes(11)));
        Should.Throw<DomainRuleViolationException>(() => direct.BackdateHistory(Start.AddDays(-1)));
    }

    [Fact]
    public void MarkHistoryRead_MovesTheReadAndDeliveredPointersWithoutEvents()
    {
        var (persona, guest) = (Guid.NewGuid(), Guid.NewGuid());
        var direct = Conversation.CreateDirect(persona, guest);
        direct.BackdateHistory(Start);
        var read = direct.AddHistoryMessage(persona, "one", Start.AddMinutes(1));
        direct.AddHistoryMessage(persona, "two", Start.AddMinutes(2));

        direct.MarkHistoryRead(guest, read);

        var member = direct.FindActiveMember(guest)!;
        (member.LastReadMessageId, member.LastReadMessageAtUtc, member.LastDeliveredMessageId)
            .ShouldBe((read.Id, Start.AddMinutes(1), read.Id));
        member.DomainEvents.ShouldBeEmpty();
        Should.Throw<DomainRuleViolationException>(() => direct.MarkHistoryRead(Guid.NewGuid(), read));
    }
}
