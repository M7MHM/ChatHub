using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;

namespace ChatHub.Api.IntegrationTests;

public sealed class UsernameTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Register_ReturnsTheUsernameAndMeShowsItWithThePrivacySetting()
    {
        var response = await Api.PostAsync("/api/auth/register",
            new { displayName = "Alice", username = "Alice_Api", email = "alice@example.com", password = Password });

        response.Status.ShouldBe(201, response.Text);
        response.Json.P("user").S("username").ShouldBe("alice_api");
        var me = await Api.GetAsync("/api/users/me", response.Json.S("accessToken"));
        me.Json.S("username").ShouldBe("alice_api");
        me.Json.B("showLastSeen").ShouldBeTrue();
        me.Json.P("usernameChangeAvailableAtUtc").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Register_TakenUsername_Returns409Problem()
    {
        await Api.PostAsync("/api/auth/register",
            new { displayName = "Alice", username = "alice", email = "alice@example.com", password = Password });

        var response = await Api.PostAsync("/api/auth/register",
            new { displayName = "Other", username = "ALICE", email = "other@example.com", password = Password });

        response.Status.ShouldBe(409);
        response.IsProblem.ShouldBeTrue();
        response.Code.ShouldBe("User.UsernameTaken");
    }

    [Fact]
    public async Task ChangeUsername_Returns200WithTheNameAndNextChangeThenTheErrorsAsProblems()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");

        var changed = await Api.PutAsync("/api/users/me/username", new { username = "Alice.New" }, alice.Token);
        var tooSoon = await Api.PutAsync("/api/users/me/username", new { username = "alice_again" }, alice.Token);
        var taken = await Api.PutAsync("/api/users/me/username", new { username = "ALICE.NEW" }, bob.Token);
        var invalid = await Api.PutAsync("/api/users/me/username", new { username = "a" }, bob.Token);
        var reserved = await Api.PutAsync("/api/users/me/username", new { username = "admin" }, bob.Token);
        var anonymous = await Api.PutAsync("/api/users/me/username", new { username = "anon_name" }, null);

        changed.Status.ShouldBe(200, changed.Text);
        changed.Json.S("username").ShouldBe("alice.new");
        changed.Json.P("nextChangeAtUtc").ValueKind.ShouldBe(JsonValueKind.String);
        (await Api.GetAsync("/api/users/me", alice.Token)).Json.S("username").ShouldBe("alice.new");
        (tooSoon.Status, tooSoon.Code, tooSoon.IsProblem).ShouldBe((429, "User.UsernameChangeTooSoon", true));
        (taken.Status, taken.Code).ShouldBe((409, "User.UsernameTaken"));
        (invalid.Status, invalid.Code).ShouldBe((400, "User.UsernameInvalid"));
        (reserved.Status, reserved.Code).ShouldBe((400, "User.UsernameReserved"));
        anonymous.Status.ShouldBe(401);
    }

    [Fact]
    public async Task UsernameAvailability_Anonymous_ReturnsOnlyTheNameAvailabilityAndReason()
    {
        var alice = await RegisterAsync("Alice");
        var aliceName = (await Api.GetAsync("/api/users/me", alice.Token)).Json.S("username");

        var free = await Api.GetAsync("/api/users/username-availability?username=Free_Name");
        var taken = await Api.GetAsync($"/api/users/username-availability?username={aliceName}");
        var reserved = await Api.GetAsync("/api/users/username-availability?username=support");
        var missing = await Api.GetAsync("/api/users/username-availability");
        var own = await Api.GetAsync($"/api/users/username-availability?username={aliceName}", alice.Token);

        free.Status.ShouldBe(200, free.Text);
        free.Json.EnumerateObject().Select(p => p.Name).ShouldBe(["username", "available", "reason"]);
        (free.Json.S("username"), free.Json.B("available"), free.Json.S("reason")).ShouldBe(("free_name", true, null));
        taken.Json.EnumerateObject().Select(p => p.Name).ShouldBe(["username", "available", "reason"]);
        (taken.Json.B("available"), taken.Json.S("reason")).ShouldBe((false, "taken"));
        taken.Text.ShouldNotContain(alice.Id.ToString());
        (reserved.Json.B("available"), reserved.Json.S("reason")).ShouldBe((false, "reserved"));
        (missing.Status, missing.Json.S("reason")).ShouldBe((200, "invalid"));
        own.Json.B("available").ShouldBeTrue();
    }

    [Fact]
    public async Task LoginAndSearch_ByUsername_FindTheUser()
    {
        await Api.PostAsync("/api/auth/register",
            new { displayName = "Zed", username = "zed_login", email = "zed@example.com", password = Password });
        var bob = await RegisterAsync("Bob");

        var login = await NewClient().PostAsync("/api/auth/login", new { identifier = "Zed_Login", password = Password });
        var search = await Api.GetAsync("/api/users/search?query=@zed_lo", bob.Token);

        login.Status.ShouldBe(200, login.Text);
        login.Json.P("user").S("username").ShouldBe("zed_login");
        search.Json.EnumerateArray().Select(u => u.S("username")).ShouldBe(["zed_login"]);
    }
}
