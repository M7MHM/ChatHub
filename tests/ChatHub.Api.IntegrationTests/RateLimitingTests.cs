using System.Diagnostics;
using System.Net.Http.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace ChatHub.Api.IntegrationTests;

public sealed class RateLimitingTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const int AuthStrictLimit = 10;
    private const int AuthSessionLimit = 60;
    private const int BurstAttempts = 3;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Login_MoreThanTenPerMinuteFromOneAddress_Returns429ProblemWithRetryAfter()
    {
        var (statuses, limited) = await InOneWindowAsync(async () =>
        {
            var client = NewClient();
            var statuses = await RepeatAsync(AuthStrictLimit + 1, () => LoginNobodyAsync(client));
            return (statuses, await LoginNobodyAsync(client));
        });

        statuses.Take(AuthStrictLimit).ShouldAllBe(status => status == 400);
        statuses[AuthStrictLimit].ShouldBe(429);
        limited.Status.ShouldBe(429);
        limited.IsProblem.ShouldBeTrue();
        int.Parse(limited.Header("Retry-After").ShouldNotBeNull()).ShouldBeInRange(1, (int)Window.TotalSeconds);
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
            new { displayName = "Carol", username = TestUsernames.For("Carol"), email = "carol@example.com", password = Password });

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
        var statuses = await InOneWindowAsync(async () =>
        {
            var client = NewClient();
            var statuses = new List<int>();
            for (var i = 0; i < 5; i++)
                statuses.Add((await client.PostAsync("/api/auth/register", new { displayName = "", password = "x" })).Status);
            statuses.AddRange(await RepeatAsync(6, () => LoginNobodyAsync(client)));
            return statuses;
        });

        statuses.Take(AuthStrictLimit).ShouldAllBe(status => status == 400);
        statuses[AuthStrictLimit].ShouldBe(429);
    }

    [Fact]
    public async Task ForgotPassword_MoreThanFivePerMinuteFromOneAddress_Returns429()
    {
        var statuses = await InOneWindowAsync(() =>
        {
            var client = NewClient();
            return RepeatAsync(6, () => client.PostAsync("/api/auth/forgot-password", new { email = "nobody@example.com" }));
        });

        statuses.Take(5).ShouldAllBe(status => status == 202);
        statuses[5].ShouldBe(429);
    }

    [Fact]
    public async Task UsernameAvailability_MoreThanThirtyPerMinuteFromOneAddress_Returns429()
    {
        var statuses = await InOneWindowAsync(() =>
        {
            var client = NewClient();
            return RepeatAsync(31, () => client.GetAsync("/api/users/username-availability?username=someone"));
        });

        statuses.Take(30).ShouldAllBe(status => status == 200);
        statuses[30].ShouldBe(429);
    }

    [Fact]
    public async Task Refresh_MoreThanSixtyPerMinuteFromOneAddress_Returns429()
    {
        var statuses = await InOneWindowAsync(() =>
        {
            var client = NewClient();
            return RepeatAsync(AuthSessionLimit + 1, () => client.PostAsync("/api/auth/refresh", new { refreshToken = "unknown-token" }));
        });

        statuses.Take(AuthSessionLimit).ShouldAllBe(status => status == 400);
        statuses[AuthSessionLimit].ShouldBe(429);
    }

    [Fact]
    public async Task Login_SpoofedForwardedForFromAnUntrustedClient_IsLimitedByTheClientAddress()
    {
        var statuses = await InOneWindowAsync(async () =>
        {
            var client = NewClient();
            var statuses = new List<int>();
            for (var i = 1; i <= AuthStrictLimit + 1; i++)
                statuses.Add((await LoginNobodyAsync(client, forwardedFor: $"203.0.113.{i}")).Status);
            return statuses;
        });

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

        var (statuses, otherClient) = await InOneWindowAsync(async () =>
        {
            var forwardedClient = ClientAddressHandler.NextAddress();
            var statuses = await RepeatAsync(AuthStrictLimit + 1, () => LoginNobodyAsync(proxy, forwardedFor: forwardedClient));
            return (statuses, await LoginNobodyAsync(proxy, forwardedFor: ClientAddressHandler.NextAddress()));
        });

        statuses[AuthStrictLimit].ShouldBe(429);
        otherClient.Status.ShouldBe(400);
    }

    [Fact]
    public async Task Login_WithConfiguredPermitLimits_UsesThemInsteadOfTheDefaults()
    {
        await using var host = Fixture.CreateFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:AuthStrictPermitLimit"] = "2",
            ["RateLimiting:AuthSessionPermitLimit"] = "3"
        });

        var (logins, refreshes) = await InOneWindowAsync(async () =>
        {
            var client = NewClient(host);
            var logins = await RepeatAsync(3, () => LoginNobodyAsync(client));
            return (logins, await RepeatAsync(4, () => client.PostAsync("/api/auth/refresh", new { refreshToken = "unknown" })));
        });

        logins.ShouldBe([400, 400, 429]);
        refreshes.ShouldBe([400, 400, 400, 429]);
    }

    private static async Task<T> InOneWindowAsync<T>(Func<Task<T>> burstFromFreshAddresses)
    {
        for (var attempt = 1; ; attempt++)
        {
            var elapsed = Stopwatch.StartNew();
            var result = await burstFromFreshAddresses();
            if (elapsed.Elapsed < Window) return result;
            if (attempt == BurstAttempts)
                throw new ShouldAssertException(
                    $"{BurstAttempts} bursts in a row took longer than the {Window.TotalSeconds:0} s rate-limit window " +
                    $"(the last took {elapsed.Elapsed.TotalSeconds:0} s), so the window may have restarted in the middle of each.");
        }
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
