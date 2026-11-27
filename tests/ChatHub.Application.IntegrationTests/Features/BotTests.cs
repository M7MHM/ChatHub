using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Bot;
using ChatHub.Application.Features.Bot.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Guests;
using ChatHub.Application.Features.Guests.Commands;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Events;
using ChatHub.Infrastructure.Bot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class BotTests(DatabaseFixture database) : AppTestBase(database)
{
    private static readonly TimeSpan ReplyDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly Guid BotId = DemoPersonas.Bot.Id;

    [Fact]
    public async Task Hi_ShowsTypingAtOnceThenRepliesAfterTheDelayThroughTheNormalSendPath()
    {
        var (guest, chat) = await GuestWithBotChatAsync();
        var mark = SignalR.Mark();

        var hi = await SendTextAsync(guest, chat, "hi");

        SignalR.Since(mark).OnlyPayload(guest, ChatEvents.UserTyping).ShouldBe(new UserTypingPayload(chat, BotId));
        (await Bot.ProcessDueAsync(CancellationToken.None)).ShouldBe(0);

        Clock.Advance(ReplyDelay);
        (await Bot.ProcessDueAsync(CancellationToken.None)).ShouldBe(1);

        var reply = SignalR.Since(mark).Where(d => d.UserId == guest && d.Method == ChatEvents.MessageReceived)
            .Select(d => (MessageResponse)d.Payload).Single(m => m.Sender.Id == BotId);
        reply.Content.ShouldBe(BotReplyTexts.Greeting);
        DomainEvents.OfType<MessageSentEvent>().ShouldContain(e => e.SenderId == BotId && e.MessageId == reply.Id);
        DomainEvents.OfType<MessageReadEvent>().ShouldContain(e => e.UserId == BotId && e.MessageId == hi.Id);
        var job = await QueryDbAsync(db => db.BotReplies.AsNoTracking().SingleAsync());
        (job.Id, job.ReplyMessageId, job.CompletedAtUtc is null).ShouldBe((hi.Id, (Guid?)reply.Id, false));
    }

    [Theory]
    [InlineData("help", BotReplyTexts.Help)]
    [InlineData("Show me the features please", BotReplyTexts.FeatureTour)]
    [InlineData("Hello there", BotReplyTexts.Greeting)]
    [InlineData("مرحبا", BotReplyTexts.Greeting)]
    [InlineData("what's up", "Interesting! Tell me more 🙂")]
    public async Task Keywords_PickTheMatchingCannedReply(string text, string expected)
    {
        var (guest, chat) = await GuestWithBotChatAsync();

        await SendTextAsync(guest, chat, text);
        Clock.Advance(ReplyDelay);
        await Bot.ProcessDueAsync(CancellationToken.None);

        (await LastBotMessageAsync(chat)).ShouldBe(expected);
    }

    [Fact]
    public async Task OtherMessages_RotateThroughTheSmallTalk()
    {
        var (guest, chat) = await GuestWithBotChatAsync();
        var replies = new List<string>();

        foreach (var text in new[] { "one", "two", "three" })
        {
            await SendTextAsync(guest, chat, text);
            Clock.Advance(ReplyDelay);
            await Bot.ProcessDueAsync(CancellationToken.None);
            replies.Add(await LastBotMessageAsync(chat));
        }

        replies.ShouldBe(BotReplyTexts.SmallTalk.Take(3));
    }

    [Fact]
    public async Task WhileAReplyIsPending_MoreMessagesGetNoExtraReply()
    {
        var (guest, chat) = await GuestWithBotChatAsync();

        await SendTextAsync(guest, chat, "hi");
        await SendTextAsync(guest, chat, "are you there?");
        await SendTextAsync(guest, chat, "hello?");
        Clock.Advance(ReplyDelay);
        await Bot.ProcessDueAsync(CancellationToken.None);

        (await QueryDbAsync(db => db.BotReplies.CountAsync())).ShouldBe(1);
        (await BotRepliesSentAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Flooding_GetsAtMostSixRepliesPerConversationPerMinute()
    {
        var (guest, chat) = await GuestWithBotChatAsync();

        for (var i = 0; i < 8; i++)
        {
            await SendTextAsync(guest, chat, $"message {i}");
            Clock.Advance(ReplyDelay);
            await Bot.ProcessDueAsync(CancellationToken.None);
        }

        (await BotRepliesSentAsync()).ShouldBe(6);

        Clock.Advance(BotSettings.FloodWindow);
        await SendTextAsync(guest, chat, "after a minute");
        Clock.Advance(ReplyDelay);
        await Bot.ProcessDueAsync(CancellationToken.None);
        (await BotRepliesSentAsync()).ShouldBe(7);
    }

    [Fact]
    public async Task TheBotsOwnReply_DoesNotTriggerAnotherReply()
    {
        var (guest, chat) = await GuestWithBotChatAsync();
        await SendTextAsync(guest, chat, "hi");
        Clock.Advance(ReplyDelay);
        await Bot.ProcessDueAsync(CancellationToken.None);

        Clock.Advance(ReplyDelay);
        (await Bot.ProcessDueAsync(CancellationToken.None)).ShouldBe(0);

        (await QueryDbAsync(db => db.BotReplies.CountAsync())).ShouldBe(1);
    }

    [Fact]
    public async Task TwoInstancesRacingForTheSameMessage_ReplyExactlyOnce()
    {
        var (guest, chat) = await GuestWithBotChatAsync();
        var hi = await SendTextAsync(guest, chat, "hi");
        Clock.Advance(ReplyDelay);

        var (a, b) = await RaceAsync(() => SendAsync(null, new SendBotReplyCommand(hi.Id)),
            () => SendAsync(null, new SendBotReplyCommand(hi.Id)));

        new[] { a.ShouldSucceed(), b.ShouldSucceed() }.Count(replied => replied).ShouldBe(1);
        (await BotRepliesSentAsync()).ShouldBe(1);
        (await Bot.ProcessDueAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task TwoProcessorsPollingAtOnce_ClaimTheJobOnce()
    {
        var (guest, chat) = await GuestWithBotChatAsync();
        await SendTextAsync(guest, chat, "hi");
        Clock.Advance(ReplyDelay);
        var other = new BotReplyService(Services.GetRequiredService<IServiceScopeFactory>(),
            Services.GetRequiredService<IOptions<BotSettings>>(), Clock, NullLogger<BotReplyService>.Instance);

        var claimed = await Task.WhenAll(Bot.ProcessDueAsync(CancellationToken.None), other.ProcessDueAsync(CancellationToken.None));

        claimed.Sum().ShouldBe(1);
        (await BotRepliesSentAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AReplyThatIsMoreThanAMinuteLate_IsDroppedInsteadOfSent()
    {
        var (guest, chat) = await GuestWithBotChatAsync();
        await SendTextAsync(guest, chat, "hi");

        Clock.Advance(ReplyDelay + BotSettings.MaxLateness + TimeSpan.FromSeconds(1));
        await Bot.ProcessDueAsync(CancellationToken.None);

        (await BotRepliesSentAsync()).ShouldBe(0);
        (await QueryDbAsync(db => db.BotReplies.AsNoTracking().SingleAsync())).CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ConversationsWithoutTheBot_ScheduleNothing()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        var direct = await CreateDirectAsync(alice, bob);

        await SendTextAsync(alice, direct, "hi bob");

        (await QueryDbAsync(db => db.BotReplies.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task RealUsers_CanChatWithTheBotToo()
    {
        (await SendAsync(null, new EnsureDemoPersonasCommand())).ShouldSucceed();
        var alice = await CreateUserAsync("Alice");
        var chat = await CreateDirectAsync(alice, BotId);

        await SendTextAsync(alice, chat, "features?");
        Clock.Advance(ReplyDelay);
        await Bot.ProcessDueAsync(CancellationToken.None);

        (await LastBotMessageAsync(chat)).ShouldBe(BotReplyTexts.FeatureTour);
    }

    private BotReplyService Bot => Services.GetRequiredService<BotReplyService>();

    private async Task<(Guid Guest, Guid Chat)> GuestWithBotChatAsync()
    {
        var guest = (await SendAsync(null, new CreateGuestCommand())).ShouldSucceed().User.Id;
        var chat = (await SendAsync(guest, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.OtherUser?.Id == BotId).Id;
        await SignalR.AddToConversationGroupAsync(guest, chat);
        return (guest, chat);
    }

    private Task<string> LastBotMessageAsync(Guid conversationId) =>
        QueryDbAsync(db => db.Messages.Where(m => m.ConversationId == conversationId && m.SenderId == BotId)
            .OrderByDescending(m => m.CreatedAtUtc).Select(m => m.Content!).FirstAsync());

    private Task<int> BotRepliesSentAsync() =>
        QueryDbAsync(db => db.BotReplies.CountAsync(r => r.ReplyMessageId != null));
}

public sealed class BotDisabledTests(DatabaseFixture database) : AppTestBase(database)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Bot:Enabled"] = "false"
    };

    [Fact]
    public async Task MessagesToTheBot_WhenTheBotIsOff_ScheduleNothing()
    {
        var guest = (await SendAsync(null, new CreateGuestCommand())).ShouldSucceed().User.Id;
        var chat = (await SendAsync(guest, new GetMyConversationsQuery())).ShouldSucceed().Items
            .Single(i => i.OtherUser?.Id == DemoPersonas.Bot.Id).Id;

        await SendTextAsync(guest, chat, "hi");

        (await QueryDbAsync(db => db.BotReplies.CountAsync())).ShouldBe(0);
    }
}
