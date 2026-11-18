using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;

namespace ChatHub.Api.IntegrationTests;

public sealed class InfrastructureEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Health_Get_Returns200Healthy()
    {
        var response = await Api.GetAsync("/health");

        response.Status.ShouldBe(200);
        response.Text.ShouldBe("Healthy");
    }

    [Fact]
    public async Task OpenApiDocument_Get_DeclaresTheBearerSecurityScheme()
    {
        var response = await Api.GetAsync("/openapi/v1.json");

        response.Status.ShouldBe(200);
        var bearer = response.Json.P("components").P("securitySchemes").P("Bearer");
        bearer.S("type").ShouldBe("http");
        bearer.S("scheme").ShouldBe("bearer");
    }

    [Fact]
    public async Task OpenApiDocument_Get_RequiresBearerOnProtectedOperationsOnly()
    {
        var paths = (await Api.GetAsync("/openapi/v1.json")).Json.P("paths");

        paths.P("/api/users/me").P("get").P("security").ValueKind.ShouldBe(JsonValueKind.Array);
        paths.P("/api/auth/login").P("post").P("security").ValueKind.ShouldBe(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task ScalarUi_InDevelopment_IsServed()
    {
        var response = await Api.GetAsync("/scalar/v1");

        response.Status.ShouldBe(200);
    }

    [Fact]
    public async Task CorsPreflight_FromTheReactDevOrigin_IsAllowedWithCredentials()
    {
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/users/me");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await Api.SendAsync(preflight);

        response.Status.ShouldBe(204);
        response.Header("Access-Control-Allow-Origin").ShouldBe("http://localhost:5173");
        response.Header("Access-Control-Allow-Credentials").ShouldBe("true");
    }
}
