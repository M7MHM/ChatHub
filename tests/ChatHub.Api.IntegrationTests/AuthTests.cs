using ChatHub.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ChatHub.Api.IntegrationTests;

public sealed class AuthTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Register_ValidEmailUser_Returns201WithLocationAndTokenPair()
    {
        var response = await Api.PostAsync("/api/auth/register",
            new { displayName = "Alice", email = "alice@example.com", password = Password });

        var userId = response.Json.P("user").G("id");
        response.Status.ShouldBe(201);
        response.Header("Location").ShouldBe($"/api/users/{userId}");
        response.Json.S("accessToken").ShouldNotBeNull().Length.ShouldBeGreaterThan(100);
        response.Json.S("refreshToken").ShouldNotBeNull().Length.ShouldBe(86);
    }

    [Fact]
    public async Task Register_ValidPhoneUser_Returns201()
    {
        var response = await Api.PostAsync("/api/auth/register",
            new { displayName = "Bob", phoneNumber = "+20 100 123 4567", password = Password });

        response.Status.ShouldBe(201);
        response.Json.P("user").G("id").ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Register_LandlinePhone_Returns400WithTheMobileNumberMessage()
    {
        var response = await Api.PostAsync("/api/auth/register",
            new { displayName = "Bob", phoneNumber = "0223456789", password = Password });

        response.Status.ShouldBe(400);
        response.HasError("PhoneNumber").ShouldBeTrue();
        response.Text.ShouldContain("Enter a valid Egyptian mobile number, e.g. 01001234567");
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409ProblemWithCodeAndTraceId()
    {
        await RegisterAsync("Alice", "alice@example.com");

        var response = await Api.PostAsync("/api/auth/register",
            new { displayName = "X", email = "ALICE@example.com", password = Password });

        response.Status.ShouldBe(409);
        response.Code.ShouldBe("Auth.ContactInUse");
        response.IsProblem.ShouldBeTrue();
    }

    [Fact]
    public async Task Register_InvalidBody_Returns400ProblemWithErrorsDictionary()
    {
        var response = await Api.PostAsync("/api/auth/register",
            new { displayName = "", email = "not-an-email", password = "short" });

        response.Status.ShouldBe(400);
        response.IsProblem.ShouldBeTrue();
        response.HasError("Email").ShouldBeTrue();
        response.HasError("Password").ShouldBeTrue();
        response.HasError("DisplayName").ShouldBeTrue();
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200ForThatUser()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.PostAsync("/api/auth/login", new { identifier = "alice@example.com", password = Password });

        response.Status.ShouldBe(200);
        response.Json.P("user").G("id").ShouldBe(alice.Id);
    }

    [Fact]
    public async Task Login_AccessToken_CarriesTheUsersSecurityStamp()
    {
        var alice = await RegisterAsync("Alice");
        var stamp = await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == alice.Id).Select(u => u.SecurityStamp).FirstAsync());

        var login = await LoginAsync("alice@example.com");

        var claim = new JsonWebTokenHandler().ReadJsonWebToken(login.Token).GetClaim("sstamp").Value;
        Guid.Parse(claim).ShouldBe(stamp);
    }

    [Fact]
    public async Task Refresh_ValidToken_Returns200WithANewPair()
    {
        await RegisterAsync("Alice");
        var login = await LoginAsync("alice@example.com");

        var response = await Api.PostAsync("/api/auth/refresh", new { refreshToken = login.RefreshToken });

        response.Status.ShouldBe(200);
        response.Json.S("refreshToken").ShouldNotBe(login.RefreshToken);
    }

    [Fact]
    public async Task Logout_Anonymous_Returns204()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.PostAsync("/api/auth/logout", new { refreshToken = alice.RefreshToken });

        response.Status.ShouldBe(204);
    }

    [Fact]
    public async Task Refresh_AfterLogout_Returns400InvalidRefreshToken()
    {
        var alice = await RegisterAsync("Alice");
        (await Api.PostAsync("/api/auth/logout", new { refreshToken = alice.RefreshToken })).Status.ShouldBe(204);

        var response = await Api.PostAsync("/api/auth/refresh", new { refreshToken = alice.RefreshToken });

        response.Status.ShouldBe(400);
        response.Code.ShouldBe("Auth.InvalidRefreshToken");
    }

    [Fact]
    public async Task ChangePassword_ValidRequest_Returns200WithANewPair()
    {
        var bob = await RegisterAsync("Bob");

        var response = await Api.PostAsync("/api/auth/change-password",
            new { currentPassword = Password, newPassword = "newpass123" }, bob.Token);

        response.Status.ShouldBe(200);
        response.Json.S("accessToken").ShouldNotBeNull();
    }

    [Fact]
    public async Task ChangePassword_Success_OldAccessTokenIsRejectedAndTheNewOneAccepted()
    {
        var bob = await RegisterAsync("Bob");

        var changed = await Api.PostAsync("/api/auth/change-password",
            new { currentPassword = Password, newPassword = "newpass123" }, bob.Token);

        (await Api.GetAsync("/api/users/me", bob.Token)).Status.ShouldBe(401);
        (await Api.GetAsync("/api/users/me", changed.Json.S("accessToken"))).Status.ShouldBe(200);
    }
}
