using System.Security.Claims;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Application.Common.RealTime;
using ChatHub.Infrastructure.RealTime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatHub.Api.IntegrationTests;

[Collection(MultiInstanceCollection.Name)]
public sealed class MultiInstanceTests(MultiInstanceFixture fixture) : ApiTestBase(fixture)
{
    private const string InstanceIdHeader = "X-Instance-Id";
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    private readonly ApiClient _host2Api = NewClient(fixture.Second);

    private TestServer Host1 => fixture.Server;
    private TestServer Host2 => fixture.SecondServer;
    private IDatabase Redis => fixture.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    [Fact]
    public async Task InstanceIdHeader_Enabled_EachHostReportsItsOwnInstance()
    {
        var first = await Api.GetAsync("/health/live");
        var second = await _host2Api.GetAsync("/health/live");

        first.Header(InstanceIdHeader).ShouldBe(fixture.Services.GetRequiredService<ServerInstance>().Id);
        second.Header(InstanceIdHeader).ShouldBe(fixture.Second.Services.GetRequiredService<ServerInstance>().Id);
        first.Header(InstanceIdHeader).ShouldNotBe(second.Header(InstanceIdHeader));
    }

    [Fact]
    public async Task SendMessage_ViaHost1_RecipientConnectedToHost2ReceivesMessageReceived()
    {
        var (alice, bob, directId) = await CreateDirectChatAsync();
        var bobHub = await ConnectAsync(Host2, bob);

        var sent = await SendTextAsync(alice, directId, "hi from host 1");

        await bobHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sent.G("id") && p.S("content") == "hi from host 1");
    }

    [Fact]
    public async Task CreateDirect_ViaHost1_OtherUserConnectedToHost2ReceivesConversationCreated()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        var bobHub = await ConnectAsync(Host2, bob);

        var directId = await CreateDirectAsync(alice, bob.Id);

        await bobHub.WaitForAsync(ChatEvents.ConversationCreated, p => p.G("id") == directId);
    }

    [Fact]
    public async Task Typing_OnHost1_PeerConnectedToHost2ReceivesUserTyping()
    {
        var (alice, bob, directId) = await CreateDirectChatAsync();
        var bobHub = await ConnectAsync(Host2, bob);
        var aliceHub = await ConnectAsync(Host1, alice);

        await aliceHub.Connection.InvokeAsync("Typing", directId);

        await bobHub.WaitForAsync(ChatEvents.UserTyping, p => p.G("userId") == alice.Id && p.G("conversationId") == directId);
        aliceHub.Events.ShouldNotContain(e => e.Name == ChatEvents.UserTyping);
    }

    [Fact]
    public async Task Presence_UserConnectsAndDisconnectsOnHost1_PeerOnHost2SeesOnlineThenOffline()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        var bobHub = await ConnectAsync(Host2, bob);

        var aliceHub = await ConnectAsync(Host1, alice);
        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && p.B("isOnline"));

        await aliceHub.Connection.StopAsync();
        await bobHub.WaitForAsync(ChatEvents.PresenceChanged,
            p => p.G("userId") == alice.Id && !p.B("isOnline") && p.S("lastSeenUtc") is not null);
    }

    [Fact]
    public async Task Presence_UserConnectedToBothHosts_GoesOfflineOnlyAfterTheLastConnectionCloses()
    {
        var (alice, bob, directId) = await CreateDirectChatAsync();
        var bobHub = await ConnectAsync(Host2, bob);
        var aliceOnHost1 = await ConnectAsync(Host1, alice);
        var aliceOnHost2 = await ConnectAsync(Host2, alice);
        (await ConnectionCountAsync(alice.Id)).ShouldBe(2);

        await aliceOnHost1.Connection.StopAsync();
        await WaitUntilAsync(async () => await ConnectionCountAsync(alice.Id) == 1);
        var sentinel = await SendTextAsync(alice, directId, "still here");
        await bobHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sentinel.G("id"));
        await Task.Delay(QuietPeriod);

        bobHub.Events.ShouldNotContain(e => IsPresence(e, alice.Id, false));
        (await IsOnlineInDatabaseAsync(alice.Id)).ShouldBeTrue();

        await aliceOnHost2.Connection.StopAsync();
        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && !p.B("isOnline"));
        (await IsOnlineInDatabaseAsync(alice.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Presence_UserConnectsToBothHostsAtOnce_GoesOnlineExactlyOnce()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        var bobHub = await ConnectAsync(Host2, bob);
        var first = new HubClient(Host1, alice.Token, skipNegotiation: true);
        var second = new HubClient(Host2, alice.Token, skipNegotiation: true);

        await Task.WhenAll(first.StartAsync(), second.StartAsync());
        await WaitUntilAsync(async () => await ConnectionCountAsync(alice.Id) == 2 && await IsOnlineInDatabaseAsync(alice.Id));
        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && p.B("isOnline"));
        await Task.Delay(QuietPeriod);
        bobHub.Events.Count(e => IsPresence(e, alice.Id, true)).ShouldBe(1);

        await Task.WhenAll(first.DisposeAsync().AsTask(), second.DisposeAsync().AsTask());
        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && !p.B("isOnline"));
        await Task.Delay(QuietPeriod);
        bobHub.Events.Count(e => IsPresence(e, alice.Id, false)).ShouldBe(1);
        (await IsOnlineInDatabaseAsync(alice.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task AddMember_ViaHost1_MemberConnectedToHost2JoinsTheGroupAndReceivesItsMessages()
    {
        var olivia = await RegisterAsync("Olivia");
        var alice = await RegisterAsync("Alice");
        var groupId = await CreateGroupAsync(olivia, "Team");
        var aliceHub = await ConnectAsync(Host2, alice);

        (await Api.PostAsync($"/api/conversations/{groupId}/members", new { userId = alice.Id }, olivia.Token)).Status.ShouldBe(204);
        var sent = await SendTextAsync(olivia, groupId, "welcome");

        await aliceHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sent.G("id"));
    }

    [Fact]
    public async Task KickMember_ViaHost1_MemberConnectedToHost2StopsReceivingTheGroupsMessages()
    {
        var olivia = await RegisterAsync("Olivia");
        var alice = await RegisterAsync("Alice");
        var groupId = await CreateGroupAsync(olivia, "Team", alice.Id);
        var directId = await CreateDirectAsync(olivia, alice.Id);
        var aliceHub = await ConnectAsync(Host2, alice);
        var before = await SendTextAsync(olivia, groupId, "before");
        await aliceHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == before.G("id"));

        (await Api.DeleteAsync($"/api/conversations/{groupId}/members/{alice.Id}", olivia.Token)).Status.ShouldBe(204);
        var after = await SendTextAsync(olivia, groupId, "after");
        var sentinel = await SendTextAsync(olivia, directId, "sentinel");

        await aliceHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sentinel.G("id"));
        await Task.Delay(QuietPeriod);
        aliceHub.Events.ShouldNotContain(e => e.Name == ChatEvents.MessageReceived && e.Payload.G("id") == after.G("id"));
    }

    [Theory]
    [InlineData("delete account")]
    [InlineData("change password")]
    public async Task CredentialChange_ViaHost1_ClosesTheUsersConnectionOnHost2(string change)
    {
        var dora = await RegisterAsync("Dora");
        var hub = await ConnectAsync(Host2, dora);

        var response = change == "delete account"
            ? await Api.DeleteAsync("/api/users/me", dora.Token)
            : await Api.PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = "newpass123" }, dora.Token);

        response.Status.ShouldBeOneOf(200, 204);
        (await hub.WaitForCloseAsync()).ShouldBeTrue();
        hub.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task CrashedInstance_HeartbeatExpires_SweeperRemovesItsConnectionsAndPeersSeeTheUserGoOffline()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        var bobHub = await ConnectAsync(Host2, bob);
        var crashedId = $"crashed-{Guid.NewGuid():N}";
        var crashed = new RedisUserConnectionTracker(
            fixture.Services.GetRequiredService<IConnectionMultiplexer>(),
            new ServerInstance(crashedId),
            Options.Create(new RealTimeSettings { HeartbeatIntervalSeconds = 1, InstanceTimeoutSeconds = 2 }),
            NullLogger<RedisUserConnectionTracker>.Instance);
        await crashed.HeartbeatAsync(new HashSet<Guid>());
        (await crashed.AddAsync(alice.Id, new StaleConnection(alice.Id))).ShouldBeTrue();
        await fixture.Services.GetRequiredService<PresenceSynchronizer>().SyncAsync(alice.Id, CancellationToken.None);
        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && p.B("isOnline"));

        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && !p.B("isOnline"));

        (await IsOnlineInDatabaseAsync(alice.Id)).ShouldBeFalse();
        (await Redis.KeyExistsAsync(RedisUserConnectionTracker.UserKey(alice.Id))).ShouldBeFalse();
        (await Redis.KeyExistsAsync(RedisUserConnectionTracker.ConnectionsKey(crashedId))).ShouldBeFalse();
        (await Redis.SetContainsAsync(RedisUserConnectionTracker.InstancesKey, crashedId)).ShouldBeFalse();
    }

    [Fact]
    public async Task GracefulShutdown_HostStops_ItsConnectionsAreRemovedAndPeersSeeTheUserGoOffline()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        var bobHub = await ConnectAsync(Host2, bob);
        await using var host3 = fixture.CreateFactory();
        var host3Id = host3.Services.GetRequiredService<ServerInstance>().Id;
        var aliceHub = await ConnectAsync(host3.Server, alice);
        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && p.B("isOnline"));

        await host3.DisposeAsync();

        await bobHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == alice.Id && !p.B("isOnline"));
        (await aliceHub.WaitForCloseAsync()).ShouldBeTrue();
        (await Redis.KeyExistsAsync(RedisUserConnectionTracker.UserKey(alice.Id))).ShouldBeFalse();
        (await Redis.KeyExistsAsync(RedisUserConnectionTracker.HeartbeatKey(host3Id))).ShouldBeFalse();
        (await Redis.SetContainsAsync(RedisUserConnectionTracker.InstancesKey, host3Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task RedisLosesItsData_HostsReRegisterTheirLiveConnectionsAndGroupChangesStillReachThem()
    {
        var olivia = await RegisterAsync("Olivia");
        var alice = await RegisterAsync("Alice");
        var groupId = await CreateGroupAsync(olivia, "Team");
        await ConnectAsync(Host1, olivia);
        var aliceHub = await ConnectAsync(Host2, alice);

        await fixture.Redis.ExecAsync(["redis-cli", "FLUSHALL"]);
        await WaitUntilAsync(async () => await ConnectionCountAsync(alice.Id) == 1 && await ConnectionCountAsync(olivia.Id) == 1);

        (await Api.PostAsync($"/api/conversations/{groupId}/members", new { userId = alice.Id }, olivia.Token)).Status.ShouldBe(204);
        var sent = await SendTextAsync(olivia, groupId, "still connected");
        await aliceHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sent.G("id"));
        (await IsOnlineInDatabaseAsync(alice.Id)).ShouldBeTrue();
    }

    private Task<HubClient> ConnectAsync(TestServer server, TestUser user) =>
        ConnectHubAsync(server, user.Id, user.Token, skipNegotiation: true);

    private async Task<(TestUser Alice, TestUser Bob, Guid DirectId)> CreateDirectChatAsync()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        return (alice, bob, await CreateDirectAsync(alice, bob.Id));
    }

    private async Task<int> ConnectionCountAsync(Guid userId) =>
        (int)await Redis.HashLengthAsync(RedisUserConnectionTracker.UserKey(userId));

    private Task<bool> IsOnlineInDatabaseAsync(Guid userId) =>
        QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == userId).Select(u => u.IsOnline).FirstAsync());

    private static bool IsPresence(HubEvent hubEvent, Guid userId, bool isOnline) =>
        hubEvent.Name == ChatEvents.PresenceChanged && hubEvent.Payload.G("userId") == userId && hubEvent.Payload.B("isOnline") == isOnline;

    private static Task WaitUntilAsync(Func<Task<bool>> condition) =>
        Eventually.UntilAsync(condition, "The connections in Redis and the presence in the database did not reach the expected state.");

    private sealed class StaleConnection(Guid userId) : HubCallerContext
    {
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => userId.ToString();
        public override ClaimsPrincipal? User { get; } = new(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
