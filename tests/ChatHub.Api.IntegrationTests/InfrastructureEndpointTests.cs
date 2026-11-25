using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;

namespace ChatHub.Api.IntegrationTests;

public sealed class InfrastructureEndpointTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_Get_Returns200Healthy(string path)
    {
        var response = await Api.GetAsync(path);

        response.Status.ShouldBe(200);
        response.Text.ShouldBe("Healthy");
    }

    [Fact]
    public async Task InstanceIdHeader_ByDefault_IsNotSent()
    {
        var response = await Api.GetAsync("/health/live");

        response.Status.ShouldBe(200);
        response.Header("X-Instance-Id").ShouldBeNull();
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

    [Theory]
    [InlineData("Production", null, 404)]
    [InlineData("Production", "true", 200)]
    [InlineData("Development", "false", 404)]
    public async Task OpenApiAndScalar_FollowTheOpenApiEnabledSetting(string environment, string? enabled, int expected)
    {
        var settings = new Dictionary<string, string?> { ["environment"] = environment, ["Https:RedirectEnabled"] = "false" };
        if (enabled is not null) settings["OpenApi:Enabled"] = enabled;
        await using var host = Fixture.CreateFactory(settings);
        var api = NewClient(host);

        (await api.GetAsync("/openapi/v1.json")).Status.ShouldBe(expected);
        (await api.GetAsync("/scalar/v1")).Status.ShouldBe(expected);
    }

    [Fact]
    public async Task CorsPreflight_WithAnEmptyConfiguredOrigin_StillAllowsTheConfiguredOne()
    {
        await using var host = Fixture.CreateFactory(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://chathub.example.app",
            ["Cors:AllowedOrigins:1"] = ""
        });
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/users/me");
        preflight.Headers.Add("Origin", "https://chathub.example.app");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await NewClient(host).SendAsync(preflight);

        response.Status.ShouldBe(204);
        response.Header("Access-Control-Allow-Origin").ShouldBe("https://chathub.example.app");
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
