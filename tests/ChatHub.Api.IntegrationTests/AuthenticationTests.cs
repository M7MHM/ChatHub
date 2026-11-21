using ChatHub.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Api.IntegrationTests;

public sealed class AuthenticationTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task ProtectedEndpoint_WithoutAToken_Returns401ProblemWithBearerChallenge()
    {
        var response = await Api.GetAsync("/api/users/me");

        response.Status.ShouldBe(401);
        response.IsProblem.ShouldBeTrue();
        response.Header("WWW-Authenticate").ShouldBe("Bearer");
    }

    [Fact]
    public async Task ProtectedEndpoint_GarbageToken_Returns401()
    {
        var response = await Api.GetAsync("/api/users/me", "not.a.jwt");

        response.Status.ShouldBe(401);
    }

    [Fact]
    public async Task ProtectedEndpoint_ValidJwtWithoutAUserId_Returns401ProblemWithBearerChallenge()
    {
        var token = Forge(new Dictionary<string, object> { ["name"] = "ghost" });

        var response = await Api.GetAsync("/api/users/me", token);

        response.Status.ShouldBe(401);
        response.IsProblem.ShouldBeTrue();
        response.Header("WWW-Authenticate").ShouldNotBeNull().ShouldStartWith("Bearer");
    }

    [Fact]
    public async Task ProtectedEndpoint_TokenWithoutASecurityStamp_Returns401()
    {
        var alice = await RegisterAsync("Alice");

        var response = await Api.GetAsync("/api/users/me", Forge(new Dictionary<string, object> { ["sub"] = alice.Id.ToString() }));

        response.Status.ShouldBe(401);
    }

    [Fact]
    public async Task ProtectedEndpoint_TokenWithAWrongSecurityStamp_Returns401()
    {
        var alice = await RegisterAsync("Alice");
        var claims = new Dictionary<string, object> { ["sub"] = alice.Id.ToString(), ["sstamp"] = Guid.NewGuid().ToString() };

        var response = await Api.GetAsync("/api/users/me", Forge(claims));

        response.Status.ShouldBe(401);
    }

    [Fact]
    public async Task ProtectedEndpoint_ForgedTokenWithTheCurrentSecurityStamp_Returns200()
    {
        var alice = await RegisterAsync("Alice");
        var stamp = await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == alice.Id).Select(u => u.SecurityStamp).FirstAsync());
        var claims = new Dictionary<string, object> { ["sub"] = alice.Id.ToString(), ["sstamp"] = stamp.ToString() };

        var response = await Api.GetAsync("/api/users/me", Forge(claims));

        response.Status.ShouldBe(200);
    }

    private string Forge(Dictionary<string, object> claims) => ForgeToken(claims, TimeSpan.FromMinutes(5));
}
