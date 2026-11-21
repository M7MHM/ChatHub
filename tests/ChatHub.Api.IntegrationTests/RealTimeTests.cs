using System.Net;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Application.Common.RealTime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Api.IntegrationTests;

public sealed class RealTimeTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Hub_ConnectWithAccessTokenQuery_Connects()
    {
        var bob = await RegisterAsync("Bob");

        var hub = await ConnectHubAsync(bob);

        hub.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Hub_ConnectWithoutAToken_IsRejectedWith401()
    {
        var hub = CreateHub(null);

        var exception = await Should.ThrowAsync<HttpRequestException>(hub.StartAsync);

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateDirect_New_OtherUserReceivesConversationCreatedWithStringEnums()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var bobHub = await ConnectHubAsync(bob);

        var directId = await CreateDirectAsync(alice, bob.Id);

        var created = await bobHub.WaitForAsync(ChatEvents.ConversationCreated, p => p.G("id") == directId);
        created.S("title").ShouldBe("Alice");
        created.S("type").ShouldBe("Direct");
        created.P("otherUser").G("id").ShouldBe(alice.Id);
    }

    [Fact]
    public async Task SendMessage_Text_OtherMemberReceivesMessageReceived()
    {
        var (alice, _, directId, bobHub) = await CreateChatWithBobListeningAsync();

        var sent = await SendTextAsync(alice, directId, "hi bob");

        await bobHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sent.G("id") && p.P("sender").G("id") == alice.Id);
    }

    [Fact]
    public async Task EditMessage_BySender_OtherMemberReceivesMessageUpdated()
    {
        var (alice, _, directId, bobHub) = await CreateChatWithBobListeningAsync();
        var sent = await SendTextAsync(alice, directId, "hi bob");

        (await Api.PutAsync($"/api/messages/{sent.G("id")}", new { content = "hi bob!" }, alice.Token)).Status.ShouldBe(200);

        await bobHub.WaitForAsync(ChatEvents.MessageUpdated, p => p.G("id") == sent.G("id") && p.S("content") == "hi bob!");
    }

    [Fact]
    public async Task DeleteMessage_BySender_OtherMemberReceivesMessageDeleted()
    {
        var (alice, _, directId, bobHub) = await CreateChatWithBobListeningAsync();
        var sent = await SendTextAsync(alice, directId, "hi bob");

        (await Api.DeleteAsync($"/api/messages/{sent.G("id")}", alice.Token)).Status.ShouldBe(204);

        await bobHub.WaitForAsync(ChatEvents.MessageDeleted, p => p.G("messageId") == sent.G("id") && p.G("conversationId") == directId);
    }

    [Fact]
    public async Task MarkAsRead_OtherMembersMessage_SenderReceivesMessagesRead()
    {
        var (alice, bob, directId, bobHub) = await CreateChatWithBobListeningAsync();
        var fromBob = await SendTextAsync(bob, directId, "hello alice");

        (await Api.PostAsync($"/api/conversations/{directId}/read", new { messageId = fromBob.G("id") }, alice.Token)).Status.ShouldBe(204);

        await bobHub.WaitForAsync(ChatEvents.MessagesRead, p => p.G("userId") == alice.Id && p.G("messageId") == fromBob.G("id"));
    }

    [Fact]
    public async Task MarkOwnMessage_ReadAndDelivered_OtherMemberReceivesNoReceipt()
    {
        var (alice, _, directId, bobHub) = await CreateChatWithBobListeningAsync();
        var mine = await SendTextAsync(alice, directId, "my own");

        (await Api.PostAsync($"/api/conversations/{directId}/read", new { messageId = mine.G("id") }, alice.Token)).Status.ShouldBe(204);
        (await Api.PostAsync($"/api/conversations/{directId}/delivered", new { messageId = mine.G("id") }, alice.Token)).Status.ShouldBe(204);
        var sentinel = await SendTextAsync(alice, directId, "sentinel");

        await bobHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sentinel.G("id"));
        bobHub.Events.ShouldNotContain(e =>
            (e.Name == ChatEvents.MessagesRead || e.Name == ChatEvents.MessagesDelivered) && e.Payload.G("messageId") == mine.G("id"));
    }

    [Fact]
    public async Task Hub_UserConnects_PeerReceivesPresenceOnline()
    {
        var (alice, _, _, bobHub) = await CreateChatWithBobListeningAsync();

        await ConnectHubAsync(alice);

        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && p.B("isOnline"));
    }

    [Fact]
    public async Task Hub_TypingInvoked_PeerReceivesUserTyping()
    {
        var (alice, _, directId, bobHub) = await CreateChatWithBobListeningAsync();
        var aliceHub = await ConnectHubAsync(alice);

        await aliceHub.Connection.InvokeAsync("Typing", directId);

        await bobHub.WaitForAsync(ChatEvents.UserTyping, p => p.G("userId") == alice.Id && p.G("conversationId") == directId);
    }

    [Fact]
    public async Task Hub_TypingRightAfterBeingAddedToTheGroup_IsAccepted()
    {
        var alice = await RegisterAsync("Alice");
        var carol = await RegisterAsync("Carol");
        var groupId = await CreateGroupAsync(alice, "Team");
        var aliceHub = await ConnectHubAsync(alice);
        var carolHub = await ConnectHubAsync(carol);
        await Should.ThrowAsync<HubException>(() => carolHub.Connection.InvokeAsync("Typing", groupId));
        TakeLoggedErrors().ShouldHaveSingleItem().Exception.ShouldBeOfType<HubException>();

        (await Api.PostAsync($"/api/conversations/{groupId}/members", new { userId = carol.Id }, alice.Token)).Status.ShouldBe(204);

        await carolHub.Connection.InvokeAsync("Typing", groupId);
        await aliceHub.WaitForAsync(ChatEvents.UserTyping, p => p.G("userId") == carol.Id && p.G("conversationId") == groupId);
    }

    [Fact]
    public async Task Hub_TypingRightAfterBeingKicked_IsRejected()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var groupId = await CreateGroupAsync(alice, "Team", bob.Id);
        var bobHub = await ConnectHubAsync(bob);
        await bobHub.Connection.InvokeAsync("Typing", groupId);

        (await Api.DeleteAsync($"/api/conversations/{groupId}/members/{bob.Id}", alice.Token)).Status.ShouldBe(204);

        var error = await Should.ThrowAsync<HubException>(() => bobHub.Connection.InvokeAsync("Typing", groupId));
        error.Message.ShouldContain("not an active member");
        TakeLoggedErrors().ShouldHaveSingleItem().Exception.ShouldBeOfType<HubException>();
    }

    [Fact]
    public async Task Hub_UserDisconnects_PeerReceivesPresenceOfflineWithLastSeen()
    {
        var (alice, _, _, bobHub) = await CreateChatWithBobListeningAsync();
        var aliceHub = await ConnectHubAsync(alice);

        await aliceHub.Connection.StopAsync();

        await bobHub.WaitForAsync(ChatEvents.PresenceChanged,
            p => p.G("userId") == alice.Id && !p.B("isOnline") && p.S("lastSeenUtc") is not null);
    }

    [Fact]
    public async Task CreateGroup_WithMember_MemberReceivesConversationCreated()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var bobHub = await ConnectHubAsync(bob);

        var groupId = await CreateGroupAsync(alice, "Team", bob.Id);

        await bobHub.WaitForAsync(ChatEvents.ConversationCreated, p => p.G("id") == groupId && p.S("title") == "Team");
    }

    [Fact]
    public async Task AddMember_ToGroup_ExistingMemberReceivesMemberAdded()
    {
        var (alice, _, carol, groupId, bobHub) = await CreateTeamWithBobListeningAsync();

        (await Api.PostAsync($"/api/conversations/{groupId}/members", new { userId = carol.Id }, alice.Token)).Status.ShouldBe(204);

        await bobHub.WaitForAsync(ChatEvents.MemberAdded,
            p => p.G("conversationId") == groupId && p.P("member").G("id") == carol.Id && p.S("role") == "Member");
    }

    [Fact]
    public async Task UpdateGroupInfo_ByOwner_MemberReceivesConversationUpdated()
    {
        var (alice, _, _, groupId, bobHub) = await CreateTeamWithBobListeningAsync();

        (await Api.PutAsync($"/api/conversations/{groupId}", new { title = "Team 2" }, alice.Token)).Status.ShouldBe(204);

        await bobHub.WaitForAsync(ChatEvents.ConversationUpdated, p => p.G("conversationId") == groupId && p.S("title") == "Team 2");
    }

    [Fact]
    public async Task KickMember_ByOwner_MemberReceivesMemberRemovedKicked()
    {
        var (alice, _, carol, groupId, bobHub) = await CreateTeamWithBobListeningAsync();
        (await Api.PostAsync($"/api/conversations/{groupId}/members", new { userId = carol.Id }, alice.Token)).Status.ShouldBe(204);

        (await Api.DeleteAsync($"/api/conversations/{groupId}/members/{carol.Id}", alice.Token)).Status.ShouldBe(204);

        await bobHub.WaitForAsync(ChatEvents.MemberRemoved, p => p.G("userId") == carol.Id && p.S("reason") == "Kicked");
    }

    [Fact]
    public async Task LeaveConversation_Group_MemberReceivesMemberRemovedLeft()
    {
        var (alice, bob, _, groupId, bobHub) = await CreateTeamWithBobListeningAsync();
        (await Api.PostAsync($"/api/conversations/{groupId}/transfer-ownership", new { newOwnerId = bob.Id }, alice.Token))
            .Status.ShouldBe(204);

        (await Api.PostAsync($"/api/conversations/{groupId}/leave", null, alice.Token)).Status.ShouldBe(204);

        await bobHub.WaitForAsync(ChatEvents.MemberRemoved, p => p.G("userId") == alice.Id && p.S("reason") == "Left");
    }

    [Fact]
    public async Task SendMessage_ImageAttachment_OtherMemberReceivesASignedUrl()
    {
        var (alice, _, directId, bobHub) = await CreateChatWithBobListeningAsync();
        var file = await UploadPngAsync(alice);

        var sent = await Api.PostAsync($"/api/conversations/{directId}/messages",
            new { type = "Image", attachmentFileId = file.G("id") }, alice.Token);

        await bobHub.WaitForAsync(ChatEvents.MessageReceived,
            p => p.G("id") == sent.Json.G("id") && p.P("attachment").S("url")?.Contains("sig=") == true);
    }

    [Fact]
    public async Task ChangePassword_WithAnOpenHubConnection_ClosesIt()
    {
        var dora = await RegisterAsync("Dora");
        var hub = await ConnectHubAsync(dora);

        var changed = await Api.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "newpass123" }, dora.Token);

        changed.Status.ShouldBe(200);
        (await hub.WaitForCloseAsync(CloseTimeout)).ShouldBeTrue();
        hub.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task ChangePassword_NewAccessToken_CanReconnectToTheHub()
    {
        var dora = await RegisterAsync("Dora");
        var hub = await ConnectHubAsync(dora);
        var changed = await Api.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "newpass123" }, dora.Token);
        (await hub.WaitForCloseAsync(CloseTimeout)).ShouldBeTrue();

        var reconnected = await ConnectHubAsync(dora.Id, changed.Json.S("accessToken")!);

        reconnected.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task DeleteMe_WithAnOpenHubConnection_ClosesIt()
    {
        var dora = await RegisterAsync("Dora");
        var hub = await ConnectHubAsync(dora);

        var deleted = await Api.DeleteAsync("/api/users/me", dora.Token);

        deleted.Status.ShouldBe(204);
        (await hub.WaitForCloseAsync(CloseTimeout)).ShouldBeTrue();
        hub.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task Hub_AccessTokenExpires_ServerClosesTheConnection()
    {
        var dora = await RegisterAsync("Dora");
        var bob = await RegisterAsync("Bob");
        var stamp = await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == dora.Id).Select(u => u.SecurityStamp).FirstAsync());
        var shortLived = ForgeToken(new Dictionary<string, object> { ["sub"] = dora.Id.ToString(), ["sstamp"] = stamp.ToString() },
            TimeSpan.FromSeconds(3));
        var doraHub = await ConnectHubAsync(dora.Id, shortLived);
        var bobHub = await ConnectHubAsync(bob);
        doraHub.State.ShouldBe(HubConnectionState.Connected);

        (await doraHub.WaitForCloseAsync(TimeSpan.FromSeconds(15))).ShouldBeTrue();

        doraHub.State.ShouldBe(HubConnectionState.Disconnected);
        bobHub.State.ShouldBe(HubConnectionState.Connected);
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid DirectId, HubClient BobHub)> CreateChatWithBobListeningAsync()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var directId = await CreateDirectAsync(alice, bob.Id);
        return (alice, bob, directId, await ConnectHubAsync(bob));
    }

    private async Task<(TestUser Alice, TestUser Bob, TestUser Carol, Guid GroupId, HubClient BobHub)> CreateTeamWithBobListeningAsync()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var carol = await RegisterAsync("Carol");
        var groupId = await CreateGroupAsync(alice, "Team", bob.Id);
        return (alice, bob, carol, groupId, await ConnectHubAsync(bob));
    }
}
