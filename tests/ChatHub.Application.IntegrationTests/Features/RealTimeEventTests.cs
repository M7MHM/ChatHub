using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Conversations.EventHandlers;
using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Messages.EventHandlers;
using ChatHub.Application.Features.Messages.Queries;
using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.EventHandlers;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class RealTimeEventTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task CreateDirectConversation_New_SendsConversationCreatedToEachUserTitledWithTheOtherName()
    {
        var paula = await CreateUserAsync("Paula");
        var quinn = await CreateUserAsync("Quinn");
        var mark = SignalR.Mark();

        var direct = await CreateDirectAsync(paula, quinn);

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(2);
        deliveries.ShouldAllBe(d => d.Method == ChatEvents.ConversationCreated && d.Route == Delivery.ToUser);
        var forPaula = deliveries.OnlyPayload(paula, ChatEvents.ConversationCreated).ShouldBeOfType<ConversationListItem>();
        forPaula.Id.ShouldBe(direct);
        forPaula.Title.ShouldBe("Quinn");
        forPaula.OtherUser.ShouldNotBeNull().Id.ShouldBe(quinn);
        var forQuinn = deliveries.OnlyPayload(quinn, ChatEvents.ConversationCreated).ShouldBeOfType<ConversationListItem>();
        forQuinn.Title.ShouldBe("Paula");
        forQuinn.OtherUser.ShouldNotBeNull().Id.ShouldBe(paula);
    }

    [Fact]
    public async Task CreateGroup_WithTwoMembers_SendsExactlyOneConversationCreatedToEachParticipant()
    {
        var paula = await CreateUserAsync("Paula");
        var quinn = await CreateUserAsync("Quinn");
        var rex = await CreateUserAsync("Rex");
        var mark = SignalR.Mark();

        var group = await CreateGroupAsync(paula, "Crew", quinn, rex);

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(3);
        deliveries.ShouldAllBe(d => d.Method == ChatEvents.ConversationCreated && d.Route == Delivery.ToUser);
        foreach (var user in new[] { paula, quinn, rex })
        {
            var item = deliveries.OnlyPayload(user, ChatEvents.ConversationCreated).ShouldBeOfType<ConversationListItem>();
            item.Title.ShouldBe("Crew");
            item.Id.ShouldBe(group);
        }
    }

    [Fact]
    public async Task SendMessage_Group_BroadcastsMessageReceivedOncePerMemberViaTheGroup()
    {
        var crew = await CreateCrewAsync();
        var mark = SignalR.Mark();

        var message = await SendTextAsync(crew.Paula, crew.Group, "hello crew");

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(3);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessageReceived, crew.Paula, crew.Quinn, crew.Rex);
        deliveries.ShouldAllBe(d => d.Payload is MessageResponse && ((MessageResponse)d.Payload).Id == message.Id);
    }

    [Fact]
    public async Task SendMessage_Image_BroadcastPayloadCarriesASignedUrl()
    {
        var yara = await CreateUserAsync("Yara");
        var zane = await CreateUserAsync("Zane");
        var direct = await CreateDirectAsync(yara, zane);
        var file = await UploadPngAsync(yara, fileName: "photo.png");
        var mark = SignalR.Mark();

        await SendFileAsync(yara, direct, MessageType.Image, file.Id);

        var received = SignalR.Since(mark).Where(d => d.Method == ChatEvents.MessageReceived).ToList();
        received.Count.ShouldBe(2);
        received.Select(d => d.Payload).ShouldAllBe(p => p is MessageResponse);
        received.Select(d => ((MessageResponse)d.Payload).Attachment.ShouldNotBeNull().Url).ShouldAllBe(url => url.Contains("sig="));
    }

    [Fact]
    public async Task AddMember_ToGroup_SendsMemberAddedToTheExistingMembersViaTheGroup()
    {
        var crew = await CreateCrewAsync();
        await SendTextAsync(crew.Paula, crew.Group, "hello crew");
        var mark = SignalR.Mark();

        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);

        var deliveries = SignalR.Since(mark);
        deliveries.ShouldReachGroupOnce(ChatEvents.MemberAdded, crew.Paula, crew.Quinn, crew.Rex);
        foreach (var payload in deliveries.Where(d => d.Method == ChatEvents.MemberAdded).Select(d => d.Payload))
        {
            var added = payload.ShouldBeOfType<MemberAddedPayload>();
            added.Role.ShouldBe(GroupRole.Member);
            added.Member.Id.ShouldBe(crew.Tess);
            added.Member.DisplayName.ShouldBe("Tess");
            added.ConversationId.ShouldBe(crew.Group);
        }
    }

    [Fact]
    public async Task AddMember_ToGroup_SendsConversationCreatedOnlyToTheAddedUserWithoutPreJoinPreview()
    {
        var crew = await CreateCrewAsync();
        await SendTextAsync(crew.Paula, crew.Group, "hello crew");
        await PauseAsync();
        var mark = SignalR.Mark();

        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(4);
        deliveries.Single(d => d.Method == ChatEvents.ConversationCreated).Route.ShouldBe(Delivery.ToUser);
        var item = deliveries.OnlyPayload(crew.Tess, ChatEvents.ConversationCreated).ShouldBeOfType<ConversationListItem>();
        item.Title.ShouldBe("Crew");
        item.LastMessage.ShouldBeNull();
        item.UnreadCount.ShouldBe(0);
    }

    [Fact]
    public async Task SendMessage_ReplyWhenSomeMembersCannotSeeTheParent_SenderSeesThePreviewButTheBroadcastHidesIt()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");
        await PauseAsync();
        await ClearChatAsync(crew.Rex, crew.Group);
        await PauseAsync();
        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);
        var mark = SignalR.Mark();

        var reply = await SendTextAsync(crew.Quinn, crew.Group, "re: hello", hello.Id);

        reply.ReplyTo.ShouldNotBeNull().Text.ShouldBe("hello crew");
        var deliveries = SignalR.Since(mark);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessageReceived, crew.Paula, crew.Quinn, crew.Rex, crew.Tess);
        foreach (var payload in deliveries.Select(d => d.Payload))
        {
            var replyTo = payload.ShouldBeOfType<MessageResponse>().ReplyTo.ShouldNotBeNull();
            replyTo.Text.ShouldBeNull();
            replyTo.SenderName.ShouldBe("Paula");
        }
    }

    [Fact]
    public async Task EditMessage_Success_BroadcastsMessageUpdatedToTheGroup()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");
        var mark = SignalR.Mark();

        (await SendAsync(crew.Paula, new EditMessageCommand("hello crew!") { MessageId = hello.Id })).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(3);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessageUpdated, crew.Paula, crew.Quinn, crew.Rex);
        foreach (var payload in deliveries.Select(d => d.Payload))
        {
            var message = payload.ShouldBeOfType<MessageResponse>();
            message.Content.ShouldBe("hello crew!");
            message.IsEdited.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task DeleteMessage_Success_BroadcastsConversationAndMessageIdsToTheGroup()
    {
        var crew = await CreateCrewAsync();
        var file = await UploadAsync(crew.Paula, FilePurpose.MessageAttachment, TestFiles.Pdf, "r.pdf", "application/pdf");
        var pdf = await SendFileAsync(crew.Paula, crew.Group, MessageType.File, file.Id);
        var mark = SignalR.Mark();

        (await SendAsync(crew.Paula, new DeleteMessageCommand(pdf.Id))).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(3);
        deliveries.ShouldReachGroupOnce(ChatEvents.MessageDeleted, crew.Paula, crew.Quinn, crew.Rex);
        deliveries.ShouldAllBe(d => Equals(d.Payload, new MessageDeletedPayload(crew.Group, pdf.Id)));
    }

    [Fact]
    public async Task ChangeMemberRole_Success_BroadcastsMemberRoleChangedToTheGroup()
    {
        var crew = await CreateCrewAsync();
        var mark = SignalR.Mark();

        (await SendAsync(crew.Paula, new ChangeMemberRoleCommand(GroupRole.Admin) { ConversationId = crew.Group, UserId = crew.Quinn }))
            .ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(3);
        deliveries.ShouldReachGroupOnce(ChatEvents.MemberRoleChanged, crew.Paula, crew.Quinn, crew.Rex);
        deliveries.ShouldAllBe(d => Equals(d.Payload, new MemberRoleChangedPayload(crew.Group, crew.Quinn, GroupRole.Admin)));
    }

    [Fact]
    public async Task UpdateGroupInfo_Success_BroadcastsConversationUpdatedToTheGroup()
    {
        var crew = await CreateCrewAsync();
        var image = await UploadPngAsync(crew.Paula, FilePurpose.GroupImage, "crew.png");
        var mark = SignalR.Mark();

        (await SendAsync(crew.Paula, new UpdateGroupInfoCommand("Crew 2", image.Id) { ConversationId = crew.Group })).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(3);
        deliveries.ShouldReachGroupOnce(ChatEvents.ConversationUpdated, crew.Paula, crew.Quinn, crew.Rex);
        deliveries.ShouldAllBe(d =>
            Equals(d.Payload, new ConversationUpdatedPayload(crew.Group, "Crew 2", FileUrls.PathFor(image.Id))));
    }

    [Fact]
    public async Task GoOnline_SharedConversations_NotifiesEachUnblockedPeerExactlyOnce()
    {
        var crew = await CreatePresenceScenarioAsync();
        var usersMark = SignalR.MarkUsersCalls();
        var mark = SignalR.Mark();

        await GoOnlineAsync(crew.Paula);

        var call = SignalR.UsersCallsSince(usersMark).ShouldHaveSingleItem();
        call.Method.ShouldBe(ChatEvents.PresenceChanged);
        call.UserIds.Count.ShouldBe(3);
        call.UserIds.ShouldBe([crew.Quinn, crew.Rex, crew.Tess], ignoreOrder: true);
        SignalR.Since(mark).ShouldAllBe(d => d.Route == Delivery.ToUsers);
    }

    [Fact]
    public async Task GoOnline_Success_SendsTheUserIdOnlineFlagAndLastSeen()
    {
        var crew = await CreatePresenceScenarioAsync();
        var mark = SignalR.Mark();

        await GoOnlineAsync(crew.Paula);

        foreach (var payload in SignalR.Since(mark).Select(d => d.Payload))
        {
            var presence = payload.ShouldBeOfType<PresenceChangedPayload>();
            presence.UserId.ShouldBe(crew.Paula);
            presence.IsOnline.ShouldBeTrue();
            presence.LastSeenUtc.ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task UpdateMyProfile_Success_SendsTheUserSummaryToTheSamePeers()
    {
        var crew = await CreatePresenceScenarioAsync();
        await GoOnlineAsync(crew.Paula);
        var avatar = await UploadPngAsync(crew.Paula, FilePurpose.Avatar, "paula.png");
        var usersMark = SignalR.MarkUsersCalls();
        var mark = SignalR.Mark();

        (await SendAsync(crew.Paula, new UpdateMyProfileCommand("Paula P.", null, avatar.Id))).ShouldSucceed();

        var call = SignalR.UsersCallsSince(usersMark).ShouldHaveSingleItem();
        call.Method.ShouldBe(ChatEvents.ProfileUpdated);
        call.UserIds.Count.ShouldBe(3);
        call.UserIds.ShouldBe([crew.Quinn, crew.Rex, crew.Tess], ignoreOrder: true);
        foreach (var payload in SignalR.Since(mark).Select(d => d.Payload))
        {
            var summary = payload.ShouldBeOfType<UserSummary>();
            summary.Id.ShouldBe(crew.Paula);
            summary.DisplayName.ShouldBe("Paula P.");
            summary.AvatarUrl.ShouldBe(FileUrls.PathFor(avatar.Id));
            summary.IsOnline.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task KickMember_Success_SendsMemberRemovedKickedOncePerMemberIncludingTheKickedUser()
    {
        var crew = await CreateCrewAsync();
        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);
        var mark = SignalR.Mark();

        (await SendAsync(crew.Paula, new KickMemberCommand(crew.Group, crew.Tess))).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(4);
        deliveries.ShouldReachGroupOnce(ChatEvents.MemberRemoved, crew.Paula, crew.Quinn, crew.Rex, crew.Tess);
        deliveries.ShouldAllBe(d => Equals(d.Payload, new MemberRemovedPayload(crew.Group, crew.Tess, MemberRemovedPayload.Kicked)));
    }

    [Fact]
    public async Task LeaveConversation_Success_SendsMemberRemovedLeftOncePerMemberIncludingTheLeaver()
    {
        var crew = await CreateCrewAsync();
        var mark = SignalR.Mark();

        (await SendAsync(crew.Quinn, new LeaveConversationCommand(crew.Group))).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        deliveries.Count.ShouldBe(3);
        deliveries.ShouldReachGroupOnce(ChatEvents.MemberRemoved, crew.Paula, crew.Quinn, crew.Rex);
        deliveries.ShouldAllBe(d => Equals(d.Payload, new MemberRemovedPayload(crew.Group, crew.Quinn, MemberRemovedPayload.Left)));
    }

    [Fact]
    public async Task SendMessage_HandlerThrows_CommandSucceedsAndTheErrorIsLogged()
    {
        var crew = await CreateCrewAsync();
        SignalR.ThrowOn = ChatEvents.MessageReceived;

        var result = await TrySendAsync(crew.Paula, crew.Group, MessageType.Text, "still saved");

        result.ShouldSucceed();
        var error = Logs.TakeErrors().ShouldHaveSingleItem();
        error.ShouldContain("MessageSentEvent");
        error.ShouldContain("SignalR down");
    }

    [Fact]
    public async Task SendMessage_HandlerThrows_TheMessageIsStillSaved()
    {
        var crew = await CreateCrewAsync();
        SignalR.ThrowOn = ChatEvents.MessageReceived;

        var sent = (await TrySendAsync(crew.Paula, crew.Group, MessageType.Text, "still saved")).ShouldSucceed();

        SignalR.ThrowOn = null;
        Logs.TakeErrors();
        var page = (await SendAsync(crew.Paula, new GetMessagesQuery(null, 1) { ConversationId = crew.Group })).ShouldSucceed();
        page.Items[0].Id.ShouldBe(sent.Id);
    }

    private async Task<Crew> CreatePresenceScenarioAsync()
    {
        var crew = await CreateCrewAsync();
        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);
        await CreateDirectAsync(crew.Paula, crew.Sam);
        await BlockAsync(crew.Sam, crew.Paula);
        await CreateDirectAsync(crew.Paula, crew.Uma);
        await BlockAsync(crew.Paula, crew.Uma);
        return crew;
    }
}
