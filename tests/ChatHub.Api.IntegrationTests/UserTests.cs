using ChatHub.Api.IntegrationTests.Infrastructure;

namespace ChatHub.Api.IntegrationTests;

public sealed class UserTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task GetMe_Authenticated_Returns200WithTheProfile()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.GetAsync("/api/users/me", alice.Token);

        response.Status.ShouldBe(200);
        response.Json.S("email").ShouldBe("alice@example.com");
        response.Json.S("displayName").ShouldBe("Alice");
    }

    [Fact]
    public async Task UpdateMe_ValidBody_Returns204()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.PutAsync("/api/users/me", new { displayName = "Alice A.", bio = "hi" }, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task UpdateMe_EmptyOrUnknownAvatarFileId_Returns400Or404()
    {
        var alice = await RegisterAsync("Alice");

        var empty = await Api.PutAsync("/api/users/me", new { displayName = "Alice", avatarFileId = Guid.Empty }, alice.Token);
        var unknown = await Api.PutAsync("/api/users/me", new { displayName = "Alice", avatarFileId = Guid.NewGuid() }, alice.Token);

        empty.Status.ShouldBe(400);
        empty.HasError("AvatarFileId").ShouldBeTrue();
        unknown.Status.ShouldBe(404);
        unknown.Code.ShouldBe("File.NotFound");
    }

    [Fact]
    public async Task SearchUsers_ByName_Returns200WithTheMatch()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob", phone: "+20 100 123 4567");

        var response = await Api.GetAsync("/api/users/search?query=bob&pageSize=5", alice.Token);

        response.Status.ShouldBe(200);
        response.Json.GetArrayLength().ShouldBe(1);
        response.Json[0].G("id").ShouldBe(bob.Id);
    }

    [Fact]
    public async Task SearchUsers_OneCharacterQuery_Returns400()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.GetAsync("/api/users/search?query=b", alice.Token);

        response.Status.ShouldBe(400);
    }

    [Fact]
    public async Task GetUserProfile_ExistingUser_Returns200()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");

        var response = await Api.GetAsync($"/api/users/{bob.Id}", alice.Token);

        response.Status.ShouldBe(200);
        response.Json.S("displayName").ShouldBe("Bob");
        response.Json.B("isBlockedByMe").ShouldBeFalse();
    }

    [Fact]
    public async Task GetUserProfile_UnknownUser_Returns404ProblemWithCode()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.GetAsync($"/api/users/{Guid.NewGuid()}", alice.Token);

        response.Status.ShouldBe(404);
        response.Code.ShouldBe("User.NotFound");
        response.IsProblem.ShouldBeTrue();
    }

    [Fact]
    public async Task BlockUser_ExistingUser_Returns204()
    {
        var alice = await RegisterAsync("Alice");
        var carol = await RegisterAsync("Carol");

        var response = await Api.PostAsync($"/api/users/{carol.Id}/block", null, alice.Token);

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task GetMyBlockedUsers_AfterBlocking_ListsTheBlockedUser()
    {
        var alice = await RegisterAsync("Alice");
        var carol = await RegisterAsync("Carol");
        (await Api.PostAsync($"/api/users/{carol.Id}/block", null, alice.Token)).Status.ShouldBe(204);

        var response = await Api.GetAsync("/api/users/me/blocked", alice.Token);

        response.Status.ShouldBe(200);
        response.Json.GetArrayLength().ShouldBe(1);
        response.Json[0].P("user").G("id").ShouldBe(carol.Id);
    }

    [Fact]
    public async Task UnblockUser_BlockedUser_Returns204()
    {
        var alice = await RegisterAsync("Alice");
        var carol = await RegisterAsync("Carol");
        (await Api.PostAsync($"/api/users/{carol.Id}/block", null, alice.Token)).Status.ShouldBe(204);

        var response = await Api.DeleteAsync($"/api/users/{carol.Id}/block", alice.Token);

        response.Status.ShouldBe(204);
    }
}
