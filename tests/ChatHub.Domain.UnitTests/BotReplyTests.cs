using ChatHub.Domain.Entities;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class BotReplyTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Schedule_UsesTheTriggerMessageIdAndIsDueAfterTheDelay()
    {
        var (message, conversation, user) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var reply = BotReply.Schedule(message, conversation, user, "  hi bot  ", Now, TimeSpan.FromMilliseconds(1500));

        (reply.Id, reply.ConversationId, reply.RequestedById, reply.TriggerText).ShouldBe((message, conversation, user, "hi bot"));
        (reply.RequestedAtUtc, reply.DueAtUtc, reply.IsCompleted).ShouldBe((Now, Now.AddMilliseconds(1500), false));
    }

    [Fact]
    public void Schedule_LongText_IsCutToTheStoredLength()
    {
        var reply = BotReply.Schedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('x', 500), Now, TimeSpan.Zero);

        reply.TriggerText!.Length.ShouldBe(BotReply.MaxTriggerTextLength);
    }

    [Fact]
    public void CompleteOrAbandon_HappensOnce()
    {
        var reply = BotReply.Schedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Now, TimeSpan.Zero);
        var replyMessage = Guid.NewGuid();

        reply.Complete(replyMessage, Now.AddSeconds(2));

        (reply.IsCompleted, reply.ReplyMessageId, reply.CompletedAtUtc).ShouldBe((true, (Guid?)replyMessage, (DateTime?)Now.AddSeconds(2)));
        Should.Throw<DomainRuleViolationException>(() => reply.Complete(Guid.NewGuid(), Now));
        Should.Throw<DomainRuleViolationException>(() => reply.Abandon(Now));
    }

    [Fact]
    public void Abandon_CompletesWithoutAReply()
    {
        var reply = BotReply.Schedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", Now, TimeSpan.Zero);

        reply.Abandon(Now);

        (reply.IsCompleted, reply.ReplyMessageId).ShouldBe((true, (Guid?)null));
    }

    [Fact]
    public void Schedule_MissingIdsOrNegativeDelay_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => BotReply.Schedule(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), null, Now, TimeSpan.Zero));
        Should.Throw<DomainRuleViolationException>(() =>
            BotReply.Schedule(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Now, TimeSpan.FromSeconds(-1)));
    }
}
