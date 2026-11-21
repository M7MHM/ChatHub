using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Api.IntegrationTests;

[Collection(RedisApiCollection.Name)]
public sealed class RedisCacheTests(RedisApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task AuthenticatedRequest_WithRedis_StoresTheStampAndProfileInRedis()
    {
        var alice = await RegisterAsync("Alice");

        (await Api.GetAsync("/api/users/me", alice.Token)).Status.ShouldBe(200);

        var keys = await fixture.RedisKeysAsync("chathub:*");
        keys.ShouldContain($"chathub:auth:stamp:{alice.Id}");
        keys.ShouldContain($"chathub:user:profile:{alice.Id}");
    }

    [Fact]
    public async Task ChangePassword_WithRedis_RemovesTheStampEntryAndRejectsTheOldTokenImmediately()
    {
        var alice = await RegisterAsync("Alice");
        var stampKey = $"chathub:auth:stamp:{alice.Id}";
        (await Api.GetAsync("/api/users/me", alice.Token)).Status.ShouldBe(200);
        (await fixture.RedisKeysAsync(stampKey)).ShouldContain(stampKey);

        var changed = await Api.PostAsync("/api/auth/change-password",
            new { currentPassword = Password, newPassword = "changed456" }, alice.Token);

        changed.Status.ShouldBe(200);
        (await fixture.RedisKeysAsync(stampKey)).ShouldBeEmpty();
        (await Api.GetAsync("/api/users/me", alice.Token)).Status.ShouldBe(401);
        (await Api.GetAsync("/api/users/me", changed.Json.S("accessToken"))).Status.ShouldBe(200);
    }

    [Fact]
    public async Task CachingDisabled_GetMe_WorksAndNothingIsWrittenToTheCache()
    {
        await using var host = fixture.CreateFactory(new Dictionary<string, string?> { ["Caching:Enabled"] = "false" });
        var api = NewClient(host);
        var registered = await api.PostAsync("/api/auth/register",
            new { displayName = "Nora", email = "nora@example.com", password = Password });
        registered.Status.ShouldBe(201);
        var token = registered.Json.S("accessToken");
        var userId = registered.Json.P("user").G("id");

        var first = await api.GetAsync("/api/users/me", token);
        var second = await api.GetAsync("/api/users/me", token);

        first.Status.ShouldBe(200);
        second.Json.S("displayName").ShouldBe("Nora");
        (await fixture.RedisKeysAsync($"chathub:*{userId}*")).ShouldBeEmpty();

        var cache = host.Services.GetRequiredService<HybridCache>();
        cache.ShouldBeOfType<PassThroughHybridCache>();
        var factoryCalls = 0;
        for (var i = 0; i < 2; i++)
            await cache.GetOrCreateAsync("probe", _ => ValueTask.FromResult(++factoryCalls));
        factoryCalls.ShouldBe(2);
        (await fixture.RedisKeysAsync("chathub:probe")).ShouldBeEmpty();
    }
}

[Collection(RedisOutageCollection.Name)]
public sealed class RedisOutageTests(RedisApiFixture fixture) : ApiTestBase(fixture)
{
    private const string HybridCacheCategory = "Microsoft.Extensions.Caching.Hybrid.HybridCache";
    private const string BackplaneCategory = "Microsoft.AspNetCore.SignalR.StackExchangeRedis.RedisHubLifetimeManager";
    private const string TrackerCategory = "ChatHub.Infrastructure.RealTime.RedisUserConnectionTracker";
    private const string DisconnectNotBroadcast = "Could not broadcast the disconnect";

    [Fact]
    public async Task RedisStopped_ApiServesFromTheDatabaseRejectsRevokedTokensAndReportsDegraded()
    {
        var alice = await RegisterAsync("Alice");
        (await Api.GetAsync("/api/users/me", alice.Token)).Status.ShouldBe(200);
        (await Api.GetAsync("/health/ready")).Text.ShouldBe("Healthy");

        await fixture.Redis.StopAsync();

        (await Api.GetAsync("/api/users/me", alice.Token)).Status.ShouldBe(200);
        (await Api.PutAsync("/api/users/me", new { displayName = "Alicia" }, alice.Token)).Status.ShouldBe(204);
        (await Api.GetAsync("/api/users/me", alice.Token)).Json.S("displayName").ShouldBe("Alicia");

        var changed = await Api.PostAsync("/api/auth/change-password",
            new { currentPassword = Password, newPassword = "changed456" }, alice.Token);
        changed.Status.ShouldBe(200);
        (await Api.GetAsync("/api/users/me", alice.Token)).Status.ShouldBe(401);
        (await Api.GetAsync("/api/users/me", changed.Json.S("accessToken"))).Status.ShouldBe(200);

        var ready = await Api.GetAsync("/health/ready");
        ready.Status.ShouldBe(200);
        ready.Text.ShouldBe("Degraded");

        var errors = TakeLoggedErrors();
        errors.ShouldContain(e => e.Category == HybridCacheCategory);
        errors.ShouldContain(e => e.Category == TrackerCategory && e.Message.StartsWith(DisconnectNotBroadcast));
        errors.ShouldAllBe(e => (e.Category == HybridCacheCategory && e.Message.StartsWith("Cache backend")) ||
                                (e.Category == TrackerCategory && e.Message.StartsWith(DisconnectNotBroadcast)) ||
                                e.Category == BackplaneCategory);
    }
}
