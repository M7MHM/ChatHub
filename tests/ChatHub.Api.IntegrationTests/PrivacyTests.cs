using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Application.Common.RealTime;
using Microsoft.AspNetCore.SignalR.Client;

namespace ChatHub.Api.IntegrationTests;

public sealed class PrivacyTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task PutPrivacy_Returns204AndMeAndOthersSeeTheEffect()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        await ConnectHubAsync(bob);

        var hidden = await Api.PutAsync("/api/users/me/privacy", new { showLastSeen = false }, bob.Token);

        hidden.Status.ShouldBe(204);
        var me = await Api.GetAsync("/api/users/me", bob.Token);
        (me.Json.B("showLastSeen"), me.Json.B("isOnline")).ShouldBe((false, true));
        var profile = await Api.GetAsync($"/api/users/{bob.Id}", alice.Token);
        profile.Json.B("isOnline").ShouldBeFalse();
        profile.Json.P("lastSeenUtc").ValueKind.ShouldBe(JsonValueKind.Null);
        var list = await Api.GetAsync("/api/conversations", alice.Token);
        var other = list.Json.P("items")[0].P("otherUser");
        (other.B("isOnline"), other.P("lastSeenUtc").ValueKind).ShouldBe((false, JsonValueKind.Null));
    }

    [Fact]
    public async Task Hiding_PeerConnectedToTheHubImmediatelySeesOfflineWithoutLastSeen()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        var aliceHub = await ConnectHubAsync(alice);
        await ConnectHubAsync(bob);
        await aliceHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == bob.Id && p.B("isOnline"));

        (await Api.PutAsync("/api/users/me/privacy", new { showLastSeen = false }, bob.Token)).Status.ShouldBe(204);

        await aliceHub.WaitForAsync(ChatEvents.PresenceChanged,
            p => p.G("userId") == bob.Id && !p.B("isOnline") && p.P("lastSeenUtc").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ShowingAgain_PeerConnectedToTheHubSeesTheRealState()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        (await Api.PutAsync("/api/users/me/privacy", new { showLastSeen = false }, bob.Token)).Status.ShouldBe(204);
        var aliceHub = await ConnectHubAsync(alice);
        await ConnectHubAsync(bob);

        (await Api.PutAsync("/api/users/me/privacy", new { showLastSeen = true }, bob.Token)).Status.ShouldBe(204);

        await aliceHub.WaitForAsync(ChatEvents.PresenceChanged,
            p => p.G("userId") == bob.Id && p.B("isOnline") && p.S("lastSeenUtc") is not null);
    }

    [Fact]
    public async Task HiddenUser_ConnectingSendsPeersNoPresence()
    {
        var (alice, bob, _) = await CreateDirectChatAsync();
        var carol = await RegisterAsync("Carol");
        await CreateDirectAsync(alice, carol.Id);
        (await Api.PutAsync("/api/users/me/privacy", new { showLastSeen = false }, bob.Token)).Status.ShouldBe(204);
        var aliceHub = await ConnectHubAsync(alice);

        await ConnectHubAsync(bob);
        await ConnectHubAsync(carol);

        await aliceHub.WaitForAsync(ChatEvents.PresenceChanged, p => p.G("userId") == carol.Id && p.B("isOnline"));
        aliceHub.Events.ShouldNotContain(e => e.Name == ChatEvents.PresenceChanged && e.Payload.G("userId") == bob.Id);
    }

    [Fact]
    public async Task ViewerWhoHidesTheirOwn_GetsNoPresenceEventsButStillGetsTyping()
    {
        var (alice, bob, directId) = await CreateDirectChatAsync();
        (await Api.PutAsync("/api/users/me/privacy", new { showLastSeen = false }, alice.Token)).Status.ShouldBe(204);
        var aliceHub = await ConnectHubAsync(alice);
        var bobHub = await ConnectHubAsync(bob);

        await bobHub.Connection.InvokeAsync("Typing", directId);

        await aliceHub.WaitForAsync(ChatEvents.UserTyping, p => p.G("userId") == bob.Id && p.G("conversationId") == directId);
        aliceHub.Events.ShouldNotContain(e => e.Name == ChatEvents.PresenceChanged);
        await bobHub.Connection.StopAsync();
        var sentinel = await SendTextAsync(alice, directId, "sentinel");
        await aliceHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sentinel.G("id"));
        aliceHub.Events.ShouldNotContain(e => e.Name == ChatEvents.PresenceChanged);
    }

    [Fact]
    public async Task MessagesPushedOverTheHub_CarryNoSenderPresence()
    {
        var (alice, bob, directId) = await CreateDirectChatAsync();
        var aliceHub = await ConnectHubAsync(alice);
        await ConnectHubAsync(bob);

        var sent = await SendTextAsync(bob, directId, "hello");

        var pushed = await aliceHub.WaitForAsync(ChatEvents.MessageReceived, p => p.G("id") == sent.G("id"));
        var sender = pushed.P("sender");
        (sender.B("isOnline"), sender.P("lastSeenUtc").ValueKind, sender.S("username") is not null)
            .ShouldBe((false, JsonValueKind.Null, true));
    }

    private async Task<(TestUser Alice, TestUser Bob, Guid DirectId)> CreateDirectChatAsync()
    {
        var alice = await RegisterAsync("Alice");
        var bob = await RegisterAsync("Bob");
        return (alice, bob, await CreateDirectAsync(alice, bob.Id));
    }
}
