using System.Text.Json;
using ChatHub.Api.IntegrationTests.Infrastructure;
using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Bot;
using ChatHub.Application.Features.Guests;
using ChatHub.Infrastructure.Guests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Api.IntegrationTests;

public sealed class GuestTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task PostGuest_Returns201WithATokenPairAndMeShowsTheGuestSession()
    {
        var response = await NewClient().PostAsync("/api/auth/guest", null);

        response.Status.ShouldBe(201, response.Text);
        response.Json.S("accessToken").ShouldNotBeNull();
        response.Json.S("refreshToken").ShouldNotBeNull();
        var user = response.Json.P("user");
        user.S("username").ShouldNotBeNull().ShouldStartWith("guest_");
        user.S("displayName").ShouldNotBeNull().ShouldStartWith("Guest ");
        response.Header("Location").ShouldBe($"/api/users/{user.G("id")}");
        var me = await Api.GetAsync("/api/users/me", response.Json.S("accessToken"));
        me.Json.B("isGuest").ShouldBeTrue();
        DateTime.Parse(me.Json.S("guestExpiresAtUtc")!).ToUniversalTime()
            .ShouldBe(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));
        me.Json.P("email").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task PostGuest_TheConversationsAreReadyWithUnreadBadges()
    {
        var guest = await CreateGuestAsync();

        var list = await Api.GetAsync("/api/conversations", guest.Token);

        var items = list.Json.P("items").EnumerateArray().ToList();
        items.Count.ShouldBe(3);
        items.Select(i => i.P("unreadCount").GetInt32()).Order().ShouldBe([2, 2, 3]);
        items.ShouldContain(i => i.P("otherUser").S("username") == "chathub_bot");
        items.ShouldContain(i => i.S("title") == GuestWorld.GroupTitle);
    }

    [Fact]
    public async Task PostGuest_SixthFromOneAddressWithinAnHour_Returns429()
    {
        var client = NewClient();
        var statuses = new List<int>();
        for (var i = 0; i < 6; i++) statuses.Add((await client.PostAsync("/api/auth/guest", null)).Status);

        statuses.ShouldBe([201, 201, 201, 201, 201, 429]);
        (await NewClient().PostAsync("/api/auth/guest", null)).Status.ShouldBe(201);
    }

    [Fact]
    public async Task PostGuest_WhenGuestModeIsOff_Returns404()
    {
        await using var host = Fixture.CreateFactory(new Dictionary<string, string?> { ["Guest:Enabled"] = "false" });

        var response = await NewClient(host).PostAsync("/api/auth/guest", null);

        (response.Status, response.Code).ShouldBe((404, "Guest.Disabled"));
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.IsGuest))).ShouldBe(0);
    }

    [Fact]
    public async Task PostGuest_AtTheGlobalCap_Returns429WithAClearCode()
    {
        await using var host = Fixture.CreateFactory(new Dictionary<string, string?> { ["Guest:MaxActive"] = "1" });

        var first = await NewClient(host).PostAsync("/api/auth/guest", null);
        var second = await NewClient(host).PostAsync("/api/auth/guest", null);

        first.Status.ShouldBe(201);
        (second.Status, second.Code, second.IsProblem).ShouldBe((429, "Guest.CapacityReached", true));
    }

    [Fact]
    public async Task Restrictions_HoldInBothDirectionsOverHttp()
    {
        var guest = await CreateGuestAsync();
        var alice = await RegisterAsync("Alice");
        var guestName = (await Api.GetAsync("/api/users/me", guest.Token)).Json.S("username");

        var guestToAlice = await Api.PostAsync("/api/conversations/direct", new { otherUserId = alice.Id }, guest.Token);
        var aliceToGuest = await Api.PostAsync("/api/conversations/direct", new { otherUserId = guest.Id }, alice.Token);
        var aliceSearch = await Api.GetAsync($"/api/users/search?query={guestName}", alice.Token);
        var aliceLookup = await Api.GetAsync($"/api/users/{guest.Id}", alice.Token);
        var guestSearch = await Api.GetAsync("/api/users/search?query=alice", guest.Token);
        var rename = await Api.PutAsync("/api/users/me/username", new { username = "real_name" }, guest.Token);
        var verify = await Api.PostAsync("/api/auth/email-verification", null, guest.Token);

        (guestToAlice.Status, guestToAlice.Code).ShouldBe((403, GuestErrors.RestrictedCode));
        (aliceToGuest.Status, aliceToGuest.Code).ShouldBe((403, GuestErrors.RestrictedCode));
        aliceSearch.Json.EnumerateArray().ShouldBeEmpty();
        aliceLookup.Status.ShouldBe(404);
        guestSearch.Json.EnumerateArray().ShouldBeEmpty();
        (rename.Status, rename.Code).ShouldBe((403, GuestErrors.RestrictedCode));
        (verify.Status, verify.Code).ShouldBe((403, GuestErrors.RestrictedCode));
        (await Api.PostAsync("/api/conversations/direct", new { otherUserId = DemoPersonas.Omar.Id }, guest.Token)).Status
            .ShouldBeOneOf(200, 201);
    }

    [Fact]
    public async Task Hi_ToTheBot_ShowsTypingThenTheReplyOverSignalR()
    {
        var guest = await CreateGuestAsync();
        var botChat = await BotChatOfAsync(guest);
        var hub = await ConnectHubAsync(guest);

        await SendTextAsync(guest, botChat, "hi");

        await hub.WaitForAsync(ChatEvents.UserTyping, p => p.G("userId") == DemoPersonas.Bot.Id && p.G("conversationId") == botChat);
        var reply = await hub.WaitForAsync(ChatEvents.MessageReceived, p => p.P("sender").G("id") == DemoPersonas.Bot.Id);
        reply.S("content").ShouldBe(BotReplyTexts.Greeting);
        var events = hub.Events.ToList();
        events.FindIndex(e => e.Name == ChatEvents.UserTyping)
            .ShouldBeLessThan(events.FindIndex(e => e.Name == ChatEvents.MessageReceived && e.Payload.P("sender").G("id") == DemoPersonas.Bot.Id));
        await hub.WaitForAsync(ChatEvents.MessagesRead, p => p.G("userId") == DemoPersonas.Bot.Id);
    }

    [Fact]
    public async Task ExpiredGuest_TokenIsRejectedAndTheCleanupRemovesItAndClosesItsConnection()
    {
        var guest = await CreateGuestAsync();
        var hub = await ConnectHubAsync(guest);
        await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == guest.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.GuestExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1))));

        (await Api.GetAsync("/api/users/me", guest.Token)).Status.ShouldBe(401);
        (await NewClient().PostAsync("/api/auth/refresh", new { refreshToken = guest.RefreshToken })).Code
            .ShouldBe("Auth.InvalidRefreshToken");

        await Fixture.Services.GetRequiredService<GuestCleanupService>().RunOnceAsync(CancellationToken.None);

        (await Eventually.TryAsync(() => QueryDbAsync(db => db.UserProfiles.AnyAsync(u => u.Id == guest.Id && u.IsDeleted))))
            .ShouldBeTrue();
        (await hub.WaitForCloseAsync()).ShouldBeTrue();
        (await QueryDbAsync(db => db.ConversationMembers.CountAsync(m => m.UserId == guest.Id))).ShouldBe(0);
    }

    [Fact]
    public async Task DemoPersonas_AreCreatedWhenAnApiInstanceStarts()
    {
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.IsDemoPersona))).ShouldBe(0);

        await using var host = Fixture.CreateFactory();
        _ = host.Server;

        var personas = await QueryDbAsync(db => db.UserProfiles.Where(u => u.IsDemoPersona).Select(u => u.Id).ToListAsync());
        personas.ShouldBe(DemoPersonas.Ids, ignoreOrder: true);
    }

    private async Task<TestUser> CreateGuestAsync()
    {
        var response = await NewClient().PostAsync("/api/auth/guest", null);
        response.Status.ShouldBe(201, response.Text);
        return TestUser.From(response.Json, null);
    }

    private async Task<Guid> BotChatOfAsync(TestUser guest)
    {
        var list = await Api.GetAsync("/api/conversations", guest.Token);
        return list.Json.P("items").EnumerateArray().Single(i => i.P("otherUser").G("id") == DemoPersonas.Bot.Id).G("id");
    }
}
