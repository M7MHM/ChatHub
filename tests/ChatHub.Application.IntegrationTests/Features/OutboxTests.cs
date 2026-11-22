using System.Text.Json;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;
using ChatHub.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using ContractConversationType = ChatHub.Contracts.Events.ConversationType;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class OutboxTests(DatabaseFixture database) : AppTestBase(database)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Messaging:Enabled"] = "true",
        ["Messaging:User"] = "guest",
        ["Messaging:Password"] = "guest"
    };

    [Fact]
    public async Task SendMessage_InAGroup_WritesMessageSentForActiveLivingMembersExceptTheSender()
    {
        var crew = await CreateCrewAsync();
        await AddMemberAsync(crew.Paula, crew.Group, crew.Sam);
        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);
        await AddMemberAsync(crew.Paula, crew.Group, crew.Uma);
        (await SendAsync(crew.Paula, new KickMemberCommand(crew.Group, crew.Sam))).ShouldSucceed();
        (await SendAsync(crew.Tess, new LeaveConversationCommand(crew.Group))).ShouldSucceed();
        (await SendAsync(crew.Rex, new MuteConversationCommand(crew.Group))).ShouldSucceed();
        await DeleteKeepingMembershipsAsync(crew.Uma);
        await ClearOutboxAsync();

        var sent = await SendTextAsync(crew.Quinn, crew.Group, "hello crew");

        var e = (await PublishedAsync<MessageSent>()).ShouldHaveSingleItem();
        e.ChatMessageId.ShouldBe(sent.Id);
        e.ConversationId.ShouldBe(crew.Group);
        e.ConversationType.ShouldBe(ContractConversationType.Group);
        e.ConversationTitle.ShouldBe("Crew");
        e.SenderId.ShouldBe(crew.Quinn);
        e.SenderDisplayName.ShouldBe("Quinn");
        e.Preview.ShouldBe("hello crew");
        e.SentAtUtc.ShouldBe(sent.CreatedAtUtc);
        e.OccurredAtUtc.ShouldBe(sent.CreatedAtUtc);
        e.RecipientUserIds.ShouldBe([crew.Paula, crew.Rex], ignoreOrder: true);
        e.MutedRecipientUserIds.ShouldBe([crew.Rex]);
        (await OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SendMessage_InADirectChat_HasNoTitleAndTargetsTheOtherUser()
    {
        var crew = await CreateCrewAsync();
        await ClearOutboxAsync();

        await SendTextAsync(crew.Paula, crew.Direct, "hi quinn");

        var e = (await PublishedAsync<MessageSent>()).ShouldHaveSingleItem();
        e.ConversationType.ShouldBe(ContractConversationType.Direct);
        e.ConversationTitle.ShouldBeNull();
        e.RecipientUserIds.ShouldBe([crew.Quinn]);
        e.MutedRecipientUserIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendMessage_ToADeletedUsersDirectChat_HasNoRecipients()
    {
        var crew = await CreateCrewAsync();
        (await SendAsync(crew.Quinn, new DeleteMyAccountCommand())).ShouldSucceed();
        await ClearOutboxAsync();

        await SendTextAsync(crew.Paula, crew.Direct, "are you there?");

        (await PublishedAsync<MessageSent>()).ShouldHaveSingleItem().RecipientUserIds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("  hello   crew  ", "hello crew")]
    [InlineData("line one\r\n\r\n  line two\tend", "line one line two end")]
    public async Task SendMessage_Preview_IsTheTextOnOneLine(string content, string expected)
    {
        var crew = await CreateCrewAsync();

        await SendTextAsync(crew.Paula, crew.Direct, content);

        (await PublishedAsync<MessageSent>()).ShouldHaveSingleItem().Preview.ShouldBe(expected);
    }

    [Fact]
    public async Task SendMessage_LongText_CutsThePreviewTo120CharactersWithAnEllipsis()
    {
        var crew = await CreateCrewAsync();
        var text = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"w{i:00}"));

        await SendTextAsync(crew.Paula, crew.Direct, text);

        var preview = (await PublishedAsync<MessageSent>()).ShouldHaveSingleItem().Preview!;
        preview.Length.ShouldBe(MessageSent.MaxPreviewLength);
        preview.ShouldBe(text[..(MessageSent.MaxPreviewLength - 1)] + "…");
    }

    [Fact]
    public async Task SendMessage_LongTextWithAnEmojiAtTheCut_NeverSplitsTheSurrogatePair()
    {
        var crew = await CreateCrewAsync();

        await SendTextAsync(crew.Paula, crew.Direct, new string('a', 118) + "😀" + new string('b', 10));

        (await PublishedAsync<MessageSent>()).ShouldHaveSingleItem().Preview.ShouldBe(new string('a', 118) + "…");
    }

    [Fact]
    public async Task SendMessage_WithAttachments_UsesAPlaceholderInsteadOfTheCaption()
    {
        var crew = await CreateCrewAsync();
        var image = await UploadPngAsync(crew.Paula);
        var voice = await UploadAsync(crew.Paula, FilePurpose.MessageAttachment, TestFiles.Ogg, "v.ogg", "audio/ogg");

        await SendFileAsync(crew.Paula, crew.Direct, MessageType.Image, image.Id, "look at this");
        await SendFileAsync(crew.Paula, crew.Direct, MessageType.Voice, voice.Id);

        (await PublishedAsync<MessageSent>()).Select(e => e.Preview).ShouldBe(["[Image]", "[Voice]"]);
    }

    [Fact]
    public async Task SystemMessage_IsNotPublished()
    {
        var crew = await CreateCrewAsync();

        await AddMessageViaDomainAsync(crew.Group, crew.Paula, MessageType.System, "Paula renamed the group");

        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkAsRead_WritesConversationReadOnlyWhenTheReadPointerAdvances()
    {
        var crew = await CreateCrewAsync();
        var first = await SendTextAsync(crew.Paula, crew.Group, "one");
        await PauseAsync();
        var second = await SendTextAsync(crew.Paula, crew.Group, "two");
        await ClearOutboxAsync();

        await MarkAsReadAsync(crew.Quinn, crew.Group, second.Id);
        await MarkAsReadAsync(crew.Quinn, crew.Group, first.Id);
        await MarkAsReadAsync(crew.Paula, crew.Group, second.Id);

        var read = (await PublishedAsync<ConversationRead>()).ShouldHaveSingleItem();
        read.ConversationId.ShouldBe(crew.Group);
        read.UserId.ShouldBe(crew.Quinn);
        read.ReadUpToUtc.ShouldBe(second.CreatedAtUtc);
        (await OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Register_WritesUserRegisteredWithTheNormalizedContact()
    {
        var auth = await RegisterAsync("Nora", email: "Nora@Example.com");
        var phoneOnly = await RegisterAsync("Omar", phone: "+201001234567");

        var events = await PublishedAsync<UserRegistered>();
        events.Count.ShouldBe(2);
        events[0].ShouldBe(new UserRegistered(events[0].MessageId, events[0].OccurredAtUtc, auth.User.Id, "nora@example.com", "Nora"));
        events[1].ShouldBe(new UserRegistered(events[1].MessageId, events[1].OccurredAtUtc, phoneOnly.User.Id, null, "Omar"));
    }

    [Fact]
    public async Task CreatedUser_IsNotARegistration()
    {
        await CreateUserAsync("Seeded");

        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateMyProfile_WritesUserProfileUpdatedOnlyForRealChangesAndStillInvalidatesTheCache()
    {
        var userId = (await RegisterAsync("Nora", email: "nora@example.com")).User.Id;
        await ClearOutboxAsync();
        (await SendAsync(userId, new GetMeQuery())).ShouldSucceed().DisplayName.ShouldBe("Nora");

        (await SendAsync(userId, new UpdateMyProfileCommand("Nora N.", "hi"))).ShouldSucceed();
        (await SendAsync(userId, new UpdateMyProfileCommand("Nora N.", "hi"))).ShouldSucceed();

        var e = (await PublishedAsync<UserProfileUpdated>()).ShouldHaveSingleItem();
        e.UserId.ShouldBe(userId);
        e.DisplayName.ShouldBe("Nora N.");
        e.Email.ShouldBe("nora@example.com");
        (await SendAsync(userId, new GetMeQuery())).ShouldSucceed().DisplayName.ShouldBe("Nora N.");
    }

    [Fact]
    public async Task DeleteMyAccount_WritesOnlyUserDeleted()
    {
        var crew = await CreateCrewAsync();

        (await SendAsync(crew.Rex, new DeleteMyAccountCommand())).ShouldSucceed();

        (await PublishedAsync<UserDeleted>()).ShouldHaveSingleItem().UserId.ShouldBe(crew.Rex);
        (await OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OtherDomainEvents_WriteNothing()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello");
        await ClearOutboxAsync();

        await AddMemberAsync(crew.Paula, crew.Group, crew.Sam);
        (await SendAsync(crew.Paula, new UpdateGroupInfoCommand("Crew 2") { ConversationId = crew.Group })).ShouldSucceed();
        (await SendAsync(crew.Paula, new EditMessageCommand("hello!") { MessageId = hello.Id })).ShouldSucceed();
        (await SendAsync(crew.Quinn, new MarkAsDeliveredCommand(hello.Id) { ConversationId = crew.Group })).ShouldSucceed();
        (await SendAsync(crew.Paula, new DeleteMessageCommand(hello.Id))).ShouldSucceed();
        await GoOnlineAsync(crew.Quinn);

        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task RolledBackTransaction_LeavesNeitherTheMessageNorItsOutboxRow()
    {
        var crew = await CreateCrewAsync();
        var messageId = Guid.Empty;

        await UseDbAsync(async db =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var conversation = await db.Conversations.Include(c => c.Members).FirstAsync(c => c.Id == crew.Group);
            var message = conversation.SendMessage(crew.Paula, MessageType.Text, "never committed");
            messageId = message.Id;
            db.Messages.Add(message);
            await db.SaveChangesAsync();

            (await db.OutboxMessages.CountAsync(m => m.Type == nameof(MessageSent))).ShouldBe(1);
            await transaction.RollbackAsync();
        });

        (await OutboxAsync()).ShouldBeEmpty();
        (await QueryDbAsync(db => db.Messages.AnyAsync(m => m.Id == messageId))).ShouldBeFalse();
    }

    [Fact]
    public async Task FailedSave_LeavesNoOutboxRow()
    {
        await CreateUserAsync("Ann", email: "ann@example.com");

        await UseDbAsync(async db =>
        {
            db.UserProfiles.Add(UserProfile.Register("Ann Again", "hash", email: Email.Create("ann@example.com")));
            await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });

        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SendMessage_EnrichmentQueryCountDoesNotGrowWithTheGroup()
    {
        var owner = await CreateUserAsync("Owner");
        var members = new List<Guid>();
        for (var i = 0; i < 8; i++) members.Add(await CreateUserAsync($"Member {i}"));
        var small = await CreateGroupAsync(owner, "Small", members[0]);
        var large = await CreateGroupAsync(owner, "Large", [.. members]);
        await SendTextAsync(owner, small, "warm up");
        await SendTextAsync(owner, large, "warm up");

        Logs.ResetSqlCommands();
        await SendTextAsync(owner, small, "hello");
        var smallGroupCommands = Logs.SqlCommands;
        Logs.ResetSqlCommands();
        await SendTextAsync(owner, large, "hello");

        Logs.SqlCommands.ShouldBe(smallGroupCommands);
        (await PublishedAsync<MessageSent>()).Last().RecipientUserIds.Count.ShouldBe(8);
    }

    private Task ClearOutboxAsync() => UseDbAsync(db => db.OutboxMessages.ExecuteDeleteAsync());

    private Task<List<OutboxMessage>> OutboxAsync() =>
        QueryDbAsync(db => db.OutboxMessages.AsNoTracking().OrderBy(m => m.OccurredAtUtc).ThenBy(m => m.Id).ToListAsync());

    private async Task<List<T>> PublishedAsync<T>() where T : IIntegrationEvent =>
        (await OutboxAsync())
            .Where(row => row.Type == typeof(T).Name)
            .Select(row =>
            {
                var integrationEvent = JsonSerializer.Deserialize<T>(row.Payload, MessageJson.Options)!;
                integrationEvent.MessageId.ShouldBe(row.Id);
                integrationEvent.OccurredAtUtc.ShouldBe(row.OccurredAtUtc);
                row.RoutingKey.ShouldBe(MessageRoutes.RoutingKeyOf(integrationEvent));
                row.ProcessedAtUtc.ShouldBeNull();
                row.Attempts.ShouldBe(0);
                return integrationEvent;
            })
            .ToList();

    private async Task MarkAsReadAsync(Guid userId, Guid conversationId, Guid messageId) =>
        (await SendAsync(userId, new MarkAsReadCommand(messageId) { ConversationId = conversationId })).ShouldSucceed();

    private Task DeleteKeepingMembershipsAsync(Guid userId) =>
        UseDbAsync(async db =>
        {
            var user = await db.UserProfiles.FirstAsync(u => u.Id == userId);
            user.Delete();
            await db.SaveChangesAsync();
        });
}

public sealed class OutboxDisabledTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task MessagingDisabled_WritesNoOutboxRowsAndRegistersNoPublisher()
    {
        var auth = await RegisterAsync("Nora", email: "nora@example.com");
        var crew = await CreateCrewAsync();
        var sent = await SendTextAsync(crew.Paula, crew.Group, "hello");
        (await SendAsync(crew.Quinn, new MarkAsReadCommand(sent.Id) { ConversationId = crew.Group })).ShouldSucceed();
        (await SendAsync(auth.User.Id, new DeleteMyAccountCommand())).ShouldSucceed();

        (await QueryDbAsync(db => db.OutboxMessages.CountAsync())).ShouldBe(0);
        Services.GetService(typeof(OutboxPublisherService)).ShouldBeNull();
        Services.GetService(typeof(RabbitMqPublisher)).ShouldBeNull();
    }
}
