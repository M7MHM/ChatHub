using ChatHub.Api.IntegrationTests.Infrastructure;

namespace ChatHub.Api.IntegrationTests;

public sealed class ConversationTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task CreateDirect_New_Returns201WithLocation()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");

        var response = await Api.PostAsync("/api/conversations/direct", new { otherUserId = bob.Id }, alice.Token);

        var id = response.Json.G("conversationId");
        response.Status.ShouldBe(201);
        response.Json.B("created").ShouldBeTrue();
        response.Header("Location").ShouldBe($"/api/conversations/{id}");
    }

    [Fact]
    public async Task CreateDirect_ExistingFromTheOtherSide_Returns200WithTheSameId()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var directId = await CreateDirectAsync(alice, bob.Id);

        var response = await Api.PostAsync("/api/conversations/direct", new { otherUserId = alice.Id }, bob.Token);

        response.Status.ShouldBe(200);
        response.Json.B("created").ShouldBeFalse();
        response.Json.G("conversationId").ShouldBe(directId);
    }

    [Fact]
    public async Task LeaveConversation_Direct_Returns422ProblemWithTheRuleMessage()
    {
        var (alice, _, directId) = await CreateAliceAndBobChatAsync();

        var response = await Api.PostAsync($"/api/conversations/{directId}/leave", null, alice.Token);

        response.Status.ShouldBe(422);
        response.IsProblem.ShouldBeTrue();
        response.Json.S("detail").ShouldBe("Direct conversations cannot be left; clear the chat instead.");
    }

    [Fact]
    public async Task GetConversation_NonMember_Returns403WithCode()
    {
        var (_, _, directId) = await CreateAliceAndBobChatAsync();
        var carol = await RegisterAsync("Carol");

        var response = await Api.GetAsync($"/api/conversations/{directId}", carol.Token);

        response.Status.ShouldBe(403);
        response.Code.ShouldBe("Conversation.NotMember");
    }

    [Fact]
    public async Task GetConversation_UnknownId_Returns404WithCode()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.GetAsync($"/api/conversations/{Guid.NewGuid()}", alice.Token);

        response.Status.ShouldBe(404);
        response.Code.ShouldBe("Conversation.NotFound");
    }

    [Fact]
    public async Task GetConversation_NonGuidId_DoesNotMatchARouteAndReturns404()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.GetAsync("/api/conversations/not-a-guid", alice.Token);

        response.Status.ShouldBe(404);
    }

    [Fact]
    public async Task CreateGroup_ValidRequest_Returns201WithLocationAndBothMembers()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");

        var response = await Api.PostAsync("/api/conversations/groups", new { title = "Team", memberIds = new[] { bob.Id } }, alice.Token);

        var id = response.Json.G("conversationId");
        response.Status.ShouldBe(201);
        response.Header("Location").ShouldBe($"/api/conversations/{id}");
        response.Json.P("memberIds").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task AddMember_ByOwner_Returns204()
    {
        var (alice, _, carol, groupId) = await CreateTeamAsync();

        var response = await Api.PostAsync($"/api/conversations/{groupId}/members", new { userId = carol.Id }, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task ChangeMemberRole_RoleAsString_Returns204()
    {
        var (alice, bob, _, groupId) = await CreateTeamAsync();

        var response = await Api.PutAsync($"/api/conversations/{groupId}/members/{bob.Id}/role", new { role = "Admin" }, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task GetConversation_Group_ReturnsRolesAsStrings()
    {
        var (alice, bob, _, groupId) = await CreateTeamAsync();
        (await Api.PutAsync($"/api/conversations/{groupId}/members/{bob.Id}/role", new { role = "Admin" }, alice.Token))
            .Status.ShouldBe(204);

        var response = await Api.GetAsync($"/api/conversations/{groupId}", alice.Token);

        response.Status.ShouldBe(200);
        response.Json.S("myRole").ShouldBe("Owner");
        response.Json.P("members").EnumerateArray().ShouldContain(m => m.P("user").G("id") == bob.Id && m.S("role") == "Admin");
    }

    [Fact]
    public async Task ChangeMemberRole_ToOwner_Returns400()
    {
        var (alice, bob, _, groupId) = await CreateTeamAsync();

        var response = await Api.PutAsync($"/api/conversations/{groupId}/members/{bob.Id}/role", new { role = "Owner" }, alice.Token);

        response.Status.ShouldBe(400);
    }

    [Fact]
    public async Task UpdateGroupInfo_ByOwner_Returns204()
    {
        var (alice, _, _, groupId) = await CreateTeamAsync();

        var response = await Api.PutAsync($"/api/conversations/{groupId}", new { title = "Team 2" }, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task KickMember_ByOwner_Returns204()
    {
        var (alice, _, carol, groupId) = await CreateTeamAsync(withCarol: true);

        var response = await Api.DeleteAsync($"/api/conversations/{groupId}/members/{carol.Id}", alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task Mute_Member_Returns204()
    {
        var (alice, _, _, groupId) = await CreateTeamAsync();

        var response = await Api.PostAsync($"/api/conversations/{groupId}/mute", null, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task GetConversations_FirstPage_ReturnsTheNewestMutedGroupAndHasMore()
    {
        var (alice, groupId, _) = await CreateMutedGroupNewerThanDirectAsync();

        var response = await Api.GetAsync("/api/conversations?pageSize=1", alice.Token);

        response.Status.ShouldBe(200);
        var first = response.Json.P("items")[0];
        first.G("id").ShouldBe(groupId);
        first.B("isMuted").ShouldBeTrue();
        response.Json.B("hasMore").ShouldBeTrue();
    }

    [Fact]
    public async Task GetConversations_SecondPageViaCursor_ReturnsTheOlderConversationAndNoMore()
    {
        var (alice, _, directId) = await CreateMutedGroupNewerThanDirectAsync();
        var first = await Api.GetAsync("/api/conversations?pageSize=1", alice.Token);

        var response = await Api.GetAsync(
            $"/api/conversations?pageSize=1&cursor={Uri.EscapeDataString(first.Json.S("nextCursor") ?? "")}", alice.Token);

        response.Status.ShouldBe(200);
        response.Json.P("items")[0].G("id").ShouldBe(directId);
        response.Json.B("hasMore").ShouldBeFalse();
    }

    [Fact]
    public async Task Unmute_MutedMember_Returns204()
    {
        var (alice, groupId, _) = await CreateMutedGroupNewerThanDirectAsync();

        var response = await Api.DeleteAsync($"/api/conversations/{groupId}/mute", alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task Clear_Member_Returns204()
    {
        var (alice, bob, directId) = await CreateAliceAndBobChatAsync();
        await SendTextAsync(bob, directId, "hello alice");

        var response = await Api.PostAsync($"/api/conversations/{directId}/clear", null, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task Clear_Member_HidesEveryEarlierMessage()
    {
        var (alice, bob, directId) = await CreateAliceAndBobChatAsync();
        await SendTextAsync(bob, directId, "hello alice");
        await SendTextAsync(alice, directId, "hi bob");

        (await Api.PostAsync($"/api/conversations/{directId}/clear", null, alice.Token)).Status.ShouldBe(204);

        var messages = await Api.GetAsync($"/api/conversations/{directId}/messages", alice.Token);
        messages.Json.P("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task TransferOwnership_ToMember_Returns204()
    {
        var (alice, bob, _, groupId) = await CreateTeamAsync();

        var response = await Api.PostAsync($"/api/conversations/{groupId}/transfer-ownership", new { newOwnerId = bob.Id }, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task LeaveConversation_GroupAfterTransferringOwnership_Returns204()
    {
        var (alice, bob, _, groupId) = await CreateTeamAsync();
        (await Api.PostAsync($"/api/conversations/{groupId}/transfer-ownership", new { newOwnerId = bob.Id }, alice.Token))
            .Status.ShouldBe(204);

        var response = await Api.PostAsync($"/api/conversations/{groupId}/leave", null, alice.Token);

        response.Status.ShouldBe(204);
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid DirectId)> CreateAliceAndBobChatAsync()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        return (alice, bob, await CreateDirectAsync(alice, bob.Id));
    }

    private async Task<(TestUser Alice, TestUser Bob, TestUser Carol, Guid GroupId)> CreateTeamAsync(bool withCarol = false)
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var carol = await RegisterAsync("Carol");
        var groupId = withCarol ? await CreateGroupAsync(alice, "Team", bob.Id, carol.Id) : await CreateGroupAsync(alice, "Team", bob.Id);
        return (alice, bob, carol, groupId);
    }

    private async Task<(TestUser Alice, Guid GroupId, Guid DirectId)> CreateMutedGroupNewerThanDirectAsync()
    {
        var (alice, bob, directId) = await CreateAliceAndBobChatAsync();
        await SendTextAsync(alice, directId, "hi bob");
        var groupId = await CreateGroupAsync(alice, "Team", bob.Id);
        (await Api.PostAsync($"/api/conversations/{groupId}/mute", null, alice.Token)).Status.ShouldBe(204);
        return (alice, groupId, directId);
    }
}
