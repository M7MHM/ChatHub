using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;

namespace ChatHub.Api.IntegrationTests;

public sealed class MessageTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task SendMessage_TextTypeAsString_Returns201WithLocation()
    {
        var (alice, _, directId) = await CreateChatAsync();

        var response = await Api.PostAsync($"/api/conversations/{directId}/messages", new { type = "Text", content = "hi bob" }, alice.Token);

        var id = response.Json.G("id");
        response.Status.ShouldBe(201);
        response.Header("Location").ShouldBe($"/api/messages/{id}");
        response.Json.S("type").ShouldBe("Text");
        response.Json.S("content").ShouldBe("hi bob");
    }

    [Fact]
    public async Task GetMessage_ThePostLocationAsMember_Returns200()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var sent = await Api.PostAsync($"/api/conversations/{directId}/messages", new { type = "Text", content = "hi bob" }, alice.Token);

        var response = await Api.GetAsync(sent.Header("Location")!, bob.Token);

        response.Status.ShouldBe(200);
        response.Json.G("id").ShouldBe(sent.Json.G("id"));
        response.Json.S("content").ShouldBe("hi bob");
    }

    [Fact]
    public async Task GetMessage_NonMember_Returns403WithCode()
    {
        var (alice, _, directId) = await CreateChatAsync();
        var carol = await RegisterAsync("Carol");
        var sent = await SendTextAsync(alice, directId, "hi bob");

        var response = await Api.GetAsync($"/api/messages/{sent.G("id")}", carol.Token);

        response.Status.ShouldBe(403);
        response.Code.ShouldBe("Conversation.NotMember");
    }

    [Fact]
    public async Task GetMessage_UnknownId_Returns404WithCode()
    {
        var (alice, _, _) = await CreateChatAsync();

        var response = await Api.GetAsync($"/api/messages/{Guid.NewGuid()}", alice.Token);

        response.Status.ShouldBe(404);
        response.Code.ShouldBe("Message.NotFound");
    }

    [Fact]
    public async Task SendMessage_SystemTypeFromAClient_Returns400()
    {
        var (alice, _, directId) = await CreateChatAsync();

        var response = await Api.PostAsync($"/api/conversations/{directId}/messages", new { type = "System", content = "x" }, alice.Token);

        response.Status.ShouldBe(400);
        response.HasError("Type").ShouldBeTrue();
    }

    [Fact]
    public async Task SendMessage_UnknownTypeString_Returns400ProblemWithErrors()
    {
        var (alice, _, directId) = await CreateChatAsync();

        var response = await Api.PostAsync($"/api/conversations/{directId}/messages", new { type = "Shout", content = "x" }, alice.Token);

        response.Status.ShouldBe(400);
        response.IsProblem.ShouldBeTrue();
        response.Json.P("errors").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task SendMessage_ImageWithoutAttachmentFileId_Returns400()
    {
        var (alice, _, directId) = await CreateChatAsync();

        var response = await Api.PostAsync($"/api/conversations/{directId}/messages", new { type = "Image" }, alice.Token);

        response.Status.ShouldBe(400);
        response.HasError("AttachmentFileId").ShouldBeTrue();
    }

    [Fact]
    public async Task EditMessage_BySender_Returns200WithTheEditedMessage()
    {
        var (alice, _, directId) = await CreateChatAsync();
        var sent = await SendTextAsync(alice, directId, "hi bob");

        var response = await Api.PutAsync($"/api/messages/{sent.G("id")}", new { content = "hi bob!" }, alice.Token);

        response.Status.ShouldBe(200);
        response.Json.S("content").ShouldBe("hi bob!");
        response.Json.B("isEdited").ShouldBeTrue();
    }

    [Fact]
    public async Task EditMessage_SomeoneElsesMessage_Returns422()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var sent = await SendTextAsync(alice, directId, "hi bob");

        var response = await Api.PutAsync($"/api/messages/{sent.G("id")}", new { content = "hacked" }, bob.Token);

        response.Status.ShouldBe(422);
    }

    [Fact]
    public async Task DeleteMessage_BySender_Returns204()
    {
        var (alice, _, directId) = await CreateChatAsync();
        var sent = await SendTextAsync(alice, directId, "hi bob");

        var response = await Api.DeleteAsync($"/api/messages/{sent.G("id")}", alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task MarkAsRead_OtherMembersMessage_Returns204()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var fromBob = await SendTextAsync(bob, directId, "hello alice");

        var response = await Api.PostAsync($"/api/conversations/{directId}/read", new { messageId = fromBob.G("id") }, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task MarkAsDelivered_MessageAlreadyMarkedRead_Returns204()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var fromBob = await SendTextAsync(bob, directId, "hello alice");
        await MarkAsync(alice, directId, "read", fromBob.G("id"));

        var response = await Api.PostAsync($"/api/conversations/{directId}/delivered", new { messageId = fromBob.G("id") }, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task MarkAsRead_UnknownMessage_Returns404MessageNotFound()
    {
        var (alice, _, directId) = await CreateChatAsync();

        var response = await Api.PostAsync($"/api/conversations/{directId}/read", new { messageId = Guid.NewGuid() }, alice.Token);

        response.Status.ShouldBe(404);
        response.Code.ShouldBe("Message.NotFound");
    }

    [Fact]
    public async Task GetReadState_AfterMarkingRead_ShowsTheReadPointer()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var fromBob = await SendTextAsync(bob, directId, "hello alice");
        await MarkAsync(alice, directId, "read", fromBob.G("id"));

        var response = await Api.GetAsync($"/api/conversations/{directId}/read-state", alice.Token);

        response.Status.ShouldBe(200);
        response.Json.EnumerateArray().ShouldContain(s => s.G("userId") == alice.Id && s.G("lastReadMessageId") == fromBob.G("id"));
    }

    [Fact]
    public async Task GetMessages_WithCursor_PagesNewestFirst()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var deleted = await SendTextAsync(alice, directId, "hi bob");
        (await Api.DeleteAsync($"/api/messages/{deleted.G("id")}", alice.Token)).Status.ShouldBe(204);
        await SendTextAsync(bob, directId, "hello alice");
        for (var i = 1; i <= 3; i++) await SendTextAsync(alice, directId, $"m{i}");

        var first = await Api.GetAsync($"/api/conversations/{directId}/messages?pageSize=2", alice.Token);
        var cursor = Uri.EscapeDataString(first.Json.S("nextCursor") ?? "");
        var second = await Api.GetAsync($"/api/conversations/{directId}/messages?pageSize=2&cursor={cursor}", alice.Token);

        first.Status.ShouldBe(200);
        first.Json.B("hasMore").ShouldBeTrue();
        first.Json.P("items").EnumerateArray().Select(m => m.S("content")).ShouldBe(["m3", "m2"]);
        second.Json.P("items").EnumerateArray().Select(m => m.S("content")).ShouldBe(["m1", "hello alice"]);
        second.Json.B("hasMore").ShouldBeTrue();
    }

    [Fact]
    public async Task GetMessages_InvalidCursor_Returns400()
    {
        var (alice, _, directId) = await CreateChatAsync();

        var response = await Api.GetAsync($"/api/conversations/{directId}/messages?cursor=garbage", alice.Token);

        response.Status.ShouldBe(400);
    }

    [Fact]
    public async Task GetMessages_NonMember_Returns403()
    {
        var (_, _, directId) = await CreateChatAsync();
        var carol = await RegisterAsync("Carol");

        var response = await Api.GetAsync($"/api/conversations/{directId}/messages", carol.Token);

        response.Status.ShouldBe(403);
    }

    [Fact]
    public async Task MarkOwnMessage_ReadAndDelivered_Returns204()
    {
        var s = await CreateOwnMessageScenarioAsync();

        var read = await Api.PostAsync($"/api/conversations/{s.DirectId}/read", new { messageId = s.MineId }, s.Alice.Token);
        var delivered = await Api.PostAsync($"/api/conversations/{s.DirectId}/delivered", new { messageId = s.MineId }, s.Alice.Token);

        read.Status.ShouldBe(204);
        delivered.Status.ShouldBe(204);
    }

    [Fact]
    public async Task MarkOwnMessage_ReadAndDelivered_LeavesTheCallersPointersUnchanged()
    {
        var s = await CreateOwnMessageScenarioAsync();

        await MarkAsync(s.Alice, s.DirectId, "read", s.MineId);
        await MarkAsync(s.Alice, s.DirectId, "delivered", s.MineId);

        var state = await Api.GetAsync($"/api/conversations/{s.DirectId}/read-state", s.Alice.Token);
        state.Json.EnumerateArray().ShouldContain(st =>
            st.G("userId") == s.Alice.Id && st.G("lastReadMessageId") == s.FromBobId && st.G("lastDeliveredMessageId") == s.FromBobId);
    }

    [Fact]
    public async Task MarkOwnMessage_AsRead_KeepsTheOtherMembersEarlierMessageUnread()
    {
        var s = await CreateOwnMessageScenarioAsync();

        await MarkAsync(s.Alice, s.DirectId, "read", s.MineId);

        var list = await Api.GetAsync("/api/conversations", s.Alice.Token);
        var direct = list.Json.P("items").EnumerateArray().First(i => i.G("id") == s.DirectId);
        direct.P("unreadCount").GetInt32().ShouldBe(1);
        direct.P("lastMessage").S("text").ShouldBe("my own");
    }

    [Fact]
    public async Task GetMessage_HiddenByClear_Returns404OnlyForTheMemberWhoCleared()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var fromBob = await SendTextAsync(bob, directId, "hello alice");
        (await Api.PostAsync($"/api/conversations/{directId}/clear", null, alice.Token)).Status.ShouldBe(204);

        var hidden = await Api.GetAsync($"/api/messages/{fromBob.G("id")}", alice.Token);
        var visible = await Api.GetAsync($"/api/messages/{fromBob.G("id")}", bob.Token);

        hidden.Status.ShouldBe(404);
        hidden.Code.ShouldBe("Message.NotFound");
        visible.Status.ShouldBe(200);
    }

    [Fact]
    public async Task GetMessage_DeletedMessage_ReturnsItMasked()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var sent = await SendTextAsync(alice, directId, "hi bob");
        (await Api.DeleteAsync($"/api/messages/{sent.G("id")}", alice.Token)).Status.ShouldBe(204);

        var response = await Api.GetAsync($"/api/messages/{sent.G("id")}", bob.Token);

        response.Status.ShouldBe(200);
        response.Json.B("isDeleted").ShouldBeTrue();
        response.Json.S("content").ShouldBeNull();
    }

    private async Task MarkAsync(TestUser user, Guid directId, string action, Guid messageId) =>
        (await Api.PostAsync($"/api/conversations/{directId}/{action}", new { messageId }, user.Token)).Status.ShouldBe(204);

    private async Task<(TestUser Alice, TestUser Bob, Guid DirectId)> CreateChatAsync()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        return (alice, bob, await CreateDirectAsync(alice, bob.Id));
    }

    private async Task<OwnMessageScenario> CreateOwnMessageScenarioAsync()
    {
        var (alice, bob, directId) = await CreateChatAsync();
        var fromBob = await SendTextAsync(bob, directId, "hello alice");
        await MarkAsync(alice, directId, "read", fromBob.G("id"));
        await SendTextAsync(bob, directId, "are you there?");
        var mine = await SendTextAsync(alice, directId, "my own");
        return new OwnMessageScenario(alice, directId, fromBob.G("id"), mine.G("id"));
    }

    private sealed record OwnMessageScenario(TestUser Alice, Guid DirectId, Guid FromBobId, Guid MineId);
}
