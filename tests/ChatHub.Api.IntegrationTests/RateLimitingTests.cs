using System.Net.Http.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace ChatHub.Api.IntegrationTests;

public sealed class RateLimitingTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const int AuthStrictLimit = 10;
    private const int AuthSessionLimit = 60;

    [Fact]
    public async Task Login_MoreThanTenPerMinuteFromOneAddress_Returns429ProblemWithRetryAfter()
    {
        var client = NewClient();

        var statuses = await RepeatAsync(AuthStrictLimit + 1, () => LoginNobodyAsync(client));
        var limited = await LoginNobodyAsync(client);

        statuses.Take(AuthStrictLimit).ShouldAllBe(status => status == 400);
        statuses[AuthStrictLimit].ShouldBe(429);
        limited.Status.ShouldBe(429);
        limited.IsProblem.ShouldBeTrue();
        limited.Header("Retry-After").ShouldNotBeNull();
    }

    [Fact]
    public async Task Login_FromAnotherAddressWhileOneIsLimited_IsAllowed()
    {
        await RegisterAsync("Alice");
        var limitedClient = NewClient();
        await RepeatAsync(AuthStrictLimit + 1, () => LoginNobodyAsync(limitedClient));

        var response = await NewClient().PostAsync("/api/auth/login", new { identifier = "alice@example.com", password = Password });

        response.Status.ShouldBe(200);
    }

    [Fact]
    public async Task Register_FromAnotherAddressWhileOneIsLimited_Returns201()
    {
        var limitedClient = NewClient();
        await RepeatAsync(AuthStrictLimit + 1, () => LoginNobodyAsync(limitedClient));

        var response = await NewClient().PostAsync("/api/auth/register",
            new { displayName = "Carol", email = "carol@example.com", password = Password });

        response.Status.ShouldBe(201);
    }

    [Fact]
    public async Task Refresh_FromAnAddressLimitedOnLogin_IsNotRejected()
    {
        await RegisterAsync("Alice");
        var session = await LoginAsync("alice@example.com");
        var client = NewClient();
        await RepeatAsync(AuthStrictLimit + 1, () => LoginNobodyAsync(client));

        var statuses = new List<int>();
        var refreshToken = session.RefreshToken;
        for (var i = 0; i < 15; i++)
        {
            var response = await client.PostAsync("/api/auth/refresh", new { refreshToken });
            statuses.Add(response.Status);
            refreshToken = response.Json.S("refreshToken") ?? refreshToken;
        }

        statuses.ShouldAllBe(status => status == 200);
    }

    [Fact]
    public async Task RegisterAndLogin_FromOneAddress_ShareTheAuthStrictBudget()
    {
        var client = NewClient();

        var statuses = new List<int>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await client.PostAsync("/api/auth/register", new { displayName = "", password = "x" })).Status);
        statuses.AddRange(await RepeatAsync(6, () => LoginNobodyAsync(client)));

        statuses.Take(AuthStrictLimit).ShouldAllBe(status => status == 400);
        statuses[AuthStrictLimit].ShouldBe(429);
    }

    [Fact]
    public async Task Refresh_MoreThanSixtyPerMinuteFromOneAddress_Returns429()
    {
        var client = NewClient();

        var statuses = new List<int>();
        for (var i = 0; i < AuthSessionLimit + 1; i++)
            statuses.Add((await client.PostAsync("/api/auth/refresh", new { refreshToken = "unknown-token" })).Status);

        statuses.Take(AuthSessionLimit).ShouldAllBe(status => status == 400);
        statuses[AuthSessionLimit].ShouldBe(429);
    }

    [Fact]
    public async Task Login_SpoofedForwardedForFromAnUntrustedClient_IsLimitedByTheClientAddress()
    {
        var client = NewClient();

        var statuses = new List<int>();
        for (var i = 1; i <= AuthStrictLimit + 1; i++)
            statuses.Add((await LoginNobodyAsync(client, forwardedFor: $"203.0.113.{i}")).Status);

        statuses.Take(AuthStrictLimit).ShouldAllBe(status => status == 400);
        statuses[AuthStrictLimit].ShouldBe(429);
    }

    [Fact]
    public async Task Login_ForwardedForFromATrustedProxy_IsLimitedByTheForwardedClient()
    {
        const string proxyAddress = "10.254.254.254";
        await using var factory = Fixture.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ForwardedHeaders:KnownProxies:0", proxyAddress));
        var proxy = new ApiClient(factory.CreateDefaultClient(new ClientAddressHandler(proxyAddress)));

        var statuses = await RepeatAsync(AuthStrictLimit + 1, () => LoginNobodyAsync(proxy, forwardedFor: "203.0.113.1"));
        var otherClient = await LoginNobodyAsync(proxy, forwardedFor: "203.0.113.2");

        statuses[AuthStrictLimit].ShouldBe(429);
        otherClient.Status.ShouldBe(400);
    }

    private static Task<ApiResponse> LoginNobodyAsync(ApiClient client) =>
        client.PostAsync("/api/auth/login", new { identifier = "nobody@example.com", password = Password });

    private static async Task<ApiResponse> LoginNobodyAsync(ApiClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { identifier = "nobody@example.com", password = Password })
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    private static async Task<List<int>> RepeatAsync(int times, Func<Task<ApiResponse>> call)
    {
        var statuses = new List<int>();
        for (var i = 0; i < times; i++) statuses.Add((await call()).Status);
        return statuses;
    }
}
