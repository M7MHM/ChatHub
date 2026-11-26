using System.Text.Json;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
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
        ["Messaging:Password"] = "guest",
        ["App:PublicApiBaseUrl"] = "https://api.chat.example.test/"
    };

    private const string ApiBase = "https://api.chat.example.test/api";

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
    public async Task Register_WritesUserRegisteredWithTheNormalizedContactPreferencesAndUnsubscribeLink()
    {
        var auth = await RegisterAsync("Nora", email: "Nora@Example.com");
        Clock.Advance(TimeSpan.FromSeconds(1));
        var phoneOnly = await RegisterAsync("Omar", phone: "+201001234567");
        var links = DomainEvents.OfType<UnsubscribeTokenIssuedEvent>().Select(e => $"{ApiBase}/email/unsubscribe?token={e.Token}").ToList();

        var events = await PublishedAsync<UserRegistered>();
        events.Count.ShouldBe(2);
        events[0].ShouldBe(new UserRegistered(events[0].MessageId, events[0].OccurredAtUtc, auth.User.Id, "nora@example.com", "Nora",
            EmailVerified: false, EmailNotificationsEnabled: true, UnsubscribeLink: links[0]));
        events[1].ShouldBe(new UserRegistered(events[1].MessageId, events[1].OccurredAtUtc, phoneOnly.User.Id, null, "Omar",
            EmailVerified: false, EmailNotificationsEnabled: true, UnsubscribeLink: links[1]));
    }

    [Fact]
    public async Task Register_WithEmail_WritesEmailVerificationRequestedLinkingToTheConfiguredApi()
    {
        var auth = await RegisterAsync("Nora", email: "nora@example.com");
        var requested = DomainEvents.OfType<EmailVerificationRequestedEvent>().ShouldHaveSingleItem();

        var e = (await PublishedAsync<EmailVerificationRequested>()).ShouldHaveSingleItem();
        e.ShouldBe(new EmailVerificationRequested(e.MessageId, e.OccurredAtUtc, auth.User.Id, "nora@example.com", "Nora",
            $"{ApiBase}/auth/verify-email?token={requested.Token}", e.ExpiresAtUtc));
        e.ExpiresAtUtc.ShouldBe(e.OccurredAtUtc + TimeSpan.FromHours(24), TimeSpan.FromMilliseconds(1));
        (await OutboxAsync()).Select(r => r.Type).ShouldBe([nameof(UserRegistered), nameof(EmailVerificationRequested)], ignoreOrder: true);
    }

    [Fact]
    public async Task RequestEmailVerification_WritesOnlyEmailVerificationRequested()
    {
        var auth = await RegisterAsync("Nora", email: "nora@example.com");
        await ClearOutboxAsync();

        (await SendAsync(auth.User.Id, new RequestEmailVerificationCommand())).ShouldSucceed();

        (await OutboxAsync()).ShouldHaveSingleItem().Type.ShouldBe(nameof(EmailVerificationRequested));
    }

    [Fact]
    public async Task VerifyEmail_WritesUserProfileUpdatedWithEmailVerifiedAndNoNewLink()
    {
        var auth = await RegisterAsync("Nora", email: "nora@example.com");
        var token = DomainEvents.OfType<EmailVerificationRequestedEvent>()[^1].Token;
        await ClearOutboxAsync();

        (await SendAsync(null, new VerifyEmailCommand(token))).ShouldSucceed();

        var e = (await PublishedAsync<UserProfileUpdated>()).ShouldHaveSingleItem();
        (e.UserId, e.Email, e.DisplayName, e.EmailVerified, e.EmailNotificationsEnabled, e.UnsubscribeLink)
            .ShouldBe((auth.User.Id, "nora@example.com", "Nora", true, true, null));
        (await OutboxAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EmailPreferenceChanges_WriteUserProfileUpdatedOnlyWhenThePreferenceChanges()
    {
        var auth = await RegisterAsync("Nora", email: "nora@example.com");
        var token = DomainEvents.OfType<UnsubscribeTokenIssuedEvent>()[^1].Token;
        await ClearOutboxAsync();

        (await SendAsync(auth.User.Id, new UpdateEmailPreferencesCommand(false))).ShouldSucceed();
        (await SendAsync(auth.User.Id, new UpdateEmailPreferencesCommand(true))).ShouldSucceed();
        (await SendAsync(null, new UnsubscribeCommand(token))).ShouldSucceed();
        (await SendAsync(null, new UnsubscribeCommand(token))).ShouldSucceed();

        (await PublishedAsync<UserProfileUpdated>()).Select(e => e.EmailNotificationsEnabled).ShouldBe([false, true, false]);
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
    public async Task DeleteMyAccount_WritesUserDeletedAndMemberLeftForEachGroup()
    {
        var crew = await CreateCrewAsync();

        (await SendAsync(crew.Rex, new DeleteMyAccountCommand())).ShouldSucceed();

        (await PublishedAsync<UserDeleted>()).ShouldHaveSingleItem().UserId.ShouldBe(crew.Rex);
        (await PublishedAsync<MemberLeft>()).ShouldHaveSingleItem().ShouldBe(new MemberLeft(default, default, crew.Group, crew.Rex),
            new MemberLeftComparer());
        (await OutboxAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task UnpublishedDomainEvents_WriteNothing()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello");
        await ClearOutboxAsync();

        await AddMemberAsync(crew.Paula, crew.Group, crew.Sam);
        (await SendAsync(crew.Paula, new UpdateGroupInfoCommand("Crew 2") { ConversationId = crew.Group })).ShouldSucceed();
        (await SendAsync(crew.Quinn, new MarkAsDeliveredCommand(hello.Id) { ConversationId = crew.Group })).ShouldSucceed();
        (await SendAsync(crew.Paula, new ChangeMemberRoleCommand(GroupRole.Admin) { ConversationId = crew.Group, UserId = crew.Quinn }))
            .ShouldSucceed();
        (await SendAsync(crew.Quinn, new MuteConversationCommand(crew.Group))).ShouldSucceed();
        await ClearOutboxAsync();
        (await SendAsync(crew.Quinn, new MuteConversationCommand(crew.Group))).ShouldSucceed();

        (await OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task EditAndDeleteForEveryone_WriteMessageEditedWithTheNewPreviewThenMessageDeleted()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello");
        await ClearOutboxAsync();

        (await SendAsync(crew.Paula, new EditMessageCommand("hello\n  everyone") { MessageId = hello.Id })).ShouldSucceed();
        (await SendAsync(crew.Paula, new DeleteMessageCommand(hello.Id))).ShouldSucceed();

        var edited = (await PublishedAsync<MessageEdited>()).ShouldHaveSingleItem();
        (edited.ChatMessageId, edited.ConversationId, edited.Preview).ShouldBe((hello.Id, crew.Group, "hello everyone"));
        var editedAt = await QueryDbAsync(db => db.Messages.Where(m => m.Id == hello.Id).Select(m => m.EditedAtUtc).SingleAsync());
        edited.EditedAtUtc.ShouldBe(editedAt!.Value, TimeSpan.FromTicks(10));
        var deleted = (await PublishedAsync<MessageDeleted>()).ShouldHaveSingleItem();
        (deleted.ChatMessageId, deleted.ConversationId).ShouldBe((hello.Id, crew.Group));
        (await OutboxAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task LeaveKickAndMute_WriteMemberLeftMemberKickedAndMuteChangesOnlyWhenTheStateChanges()
    {
        var crew = await CreateCrewAsync();
        await ClearOutboxAsync();

        (await SendAsync(crew.Paula, new KickMemberCommand(crew.Group, crew.Rex))).ShouldSucceed();
        (await SendAsync(crew.Quinn, new MuteConversationCommand(crew.Group))).ShouldSucceed();
        (await SendAsync(crew.Quinn, new MuteConversationCommand(crew.Group))).ShouldSucceed();
        Clock.Advance(TimeSpan.FromSeconds(1));
        (await SendAsync(crew.Quinn, new UnmuteConversationCommand(crew.Group))).ShouldSucceed();
        (await SendAsync(crew.Quinn, new LeaveConversationCommand(crew.Group))).ShouldSucceed();

        var kicked = (await PublishedAsync<MemberKicked>()).ShouldHaveSingleItem();
        (kicked.ConversationId, kicked.UserId, kicked.KickedByUserId).ShouldBe((crew.Group, crew.Rex, crew.Paula));
        (await PublishedAsync<ConversationMuteChanged>()).Select(e => (e.ConversationId, e.UserId, e.IsMuted))
            .ShouldBe([(crew.Group, crew.Quinn, true), (crew.Group, crew.Quinn, false)]);
        var left = (await PublishedAsync<MemberLeft>()).ShouldHaveSingleItem();
        (left.ConversationId, left.UserId).ShouldBe((crew.Group, crew.Quinn));
    }

    [Fact]
    public async Task PresenceChanges_WriteUserStatusChanged()
    {
        var crew = await CreateCrewAsync();

        await GoOnlineAsync(crew.Quinn);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await UseDbAsync(async db =>
        {
            (await db.UserProfiles.FirstAsync(u => u.Id == crew.Quinn)).GoOffline();
            await db.SaveChangesAsync();
        });

        (await PublishedAsync<UserStatusChanged>()).Select(e => (e.UserId, e.IsOnline)).ShouldBe([(crew.Quinn, true), (crew.Quinn, false)]);
    }

    private sealed class MemberLeftComparer : IEqualityComparer<MemberLeft>
    {
        public bool Equals(MemberLeft? x, MemberLeft? y) => (x?.ConversationId, x?.UserId) == (y?.ConversationId, y?.UserId);

        public int GetHashCode(MemberLeft obj) => HashCode.Combine(obj.ConversationId, obj.UserId);
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
            db.UserProfiles.Add(UserProfile.Register(Username.Create(TestUsernames.For("Ann Again")), "Ann Again", "hash",
                email: Email.Create("ann@example.com")));
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
