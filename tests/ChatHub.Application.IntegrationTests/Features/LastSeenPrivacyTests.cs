using ChatHub.Application.Common.RealTime;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Messages.Queries;
using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.EventHandlers;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class LastSeenPrivacyTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task ByDefault_PeersSeePresenceInEveryProjection()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);

        var views = await PresenceOfBobSeenByAsync(t.Alice, t);

        views.ShouldAllBe(v => v.IsOnline && v.LastSeenUtc != null);
    }

    [Fact]
    public async Task HiddenUser_IsOfflineWithoutLastSeenInTheListDetailsProfileAndSearch()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        await PresenceOfBobSeenByAsync(t.Alice, t);

        await SetShowLastSeenAsync(t.Bob, false);

        var views = await PresenceOfBobSeenByAsync(t.Alice, t);
        views.ShouldAllBe(v => !v.IsOnline && v.LastSeenUtc == null);
        views.Count.ShouldBe(4);
    }

    [Fact]
    public async Task ViewerWhoHidesTheirOwn_SeesNoOnesPresence()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);

        await SetShowLastSeenAsync(t.Alice, false);

        (await PresenceOfBobSeenByAsync(t.Alice, t)).ShouldAllBe(v => !v.IsOnline && v.LastSeenUtc == null);
        (await PresenceOfBobSeenByAsync(t.Carol, t)).ShouldAllBe(v => v.IsOnline && v.LastSeenUtc != null);
    }

    [Fact]
    public async Task HiddenUser_StillSeesTheirOwnStatus()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        await SetShowLastSeenAsync(t.Bob, false);

        var me = (await SendAsync(t.Bob, new GetMeQuery())).ShouldSucceed();
        var ownMember = (await SendAsync(t.Bob, new GetConversationDetailsQuery(t.Group))).ShouldSucceed()
            .Members.Single(m => m.User.Id == t.Bob).User;
        var ownProfile = (await SendAsync(t.Bob, new GetUserProfileQuery(t.Bob))).ShouldSucceed();

        (me.ShowLastSeen, me.IsOnline, me.LastSeenUtc is null).ShouldBe((false, true, false));
        (ownMember.IsOnline, ownMember.LastSeenUtc is null).ShouldBe((true, false));
        (ownProfile.IsOnline, ownProfile.LastSeenUtc is null).ShouldBe((true, false));
    }

    [Fact]
    public async Task BlockedUsersList_HidesPresenceByBothRules()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        await BlockAsync(t.Alice, t.Bob);
        BlockedSummary(await SendAsync(t.Alice, new GetMyBlockedUsersQuery())).IsOnline.ShouldBeTrue();

        await SetShowLastSeenAsync(t.Bob, false);
        BlockedSummary(await SendAsync(t.Alice, new GetMyBlockedUsersQuery())).IsOnline.ShouldBeFalse();

        await SetShowLastSeenAsync(t.Bob, true);
        await SetShowLastSeenAsync(t.Alice, false);
        var hidden = BlockedSummary(await SendAsync(t.Alice, new GetMyBlockedUsersQuery()));
        (hidden.IsOnline, hidden.LastSeenUtc).ShouldBe((false, (DateTime?)null));
    }

    [Fact]
    public async Task Messages_NeverCarryTheSendersPresence()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        var mark = SignalR.Mark();

        var sent = await SendTextAsync(t.Bob, t.Direct, "hi");
        var listed = (await SendAsync(t.Alice, new GetMessagesQuery { ConversationId = t.Direct })).ShouldSucceed().Items.Single();
        var pushed = SignalR.Since(mark).OnlyPayload(t.Alice, ChatEvents.MessageReceived).ShouldBeOfType<MessageResponse>();

        foreach (var sender in new[] { sent.Sender, listed.Sender, pushed.Sender })
            (sender.Id, sender.Username, sender.IsOnline, sender.LastSeenUtc).ShouldBe((t.Bob, sender.Username, false, (DateTime?)null));
        sent.Sender.Username.ShouldNotBeNull();
    }

    [Fact]
    public async Task GoOnline_HiddenUser_SendsNoPresenceChanged()
    {
        var t = await CreateTrioAsync();
        await SetShowLastSeenAsync(t.Bob, false);
        var mark = SignalR.Mark();

        await GoOnlineAsync(t.Bob);

        DomainEvents.OfType<UserStatusChangedEvent>().ShouldContain(e => e.UserId == t.Bob && e.IsOnline);
        SignalR.Since(mark).ShouldNotContain(d => d.Method == ChatEvents.PresenceChanged);
    }

    [Fact]
    public async Task GoOnline_IsSentOnlyToPeersWhoShowTheirOwnLastSeen()
    {
        var t = await CreateTrioAsync();
        await SetShowLastSeenAsync(t.Carol, false);
        var mark = SignalR.Mark();

        await GoOnlineAsync(t.Bob);

        var deliveries = SignalR.Since(mark).Where(d => d.Method == ChatEvents.PresenceChanged).ToList();
        deliveries.Select(d => d.UserId).ShouldBe([t.Alice]);
        var presence = deliveries[0].Payload.ShouldBeOfType<PresenceChangedPayload>();
        (presence.UserId, presence.IsOnline, presence.LastSeenUtc is null).ShouldBe((t.Bob, true, false));
    }

    [Fact]
    public async Task ProfileUpdated_PeersWhoHideTheirOwnGetTheSummaryWithoutPresence()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        await SetShowLastSeenAsync(t.Carol, false);
        var mark = SignalR.Mark();

        (await SendAsync(t.Bob, new UpdateMyProfileCommand("Bobby", null))).ShouldSucceed();

        var deliveries = SignalR.Since(mark);
        var toAlice = deliveries.OnlyPayload(t.Alice, ChatEvents.ProfileUpdated).ShouldBeOfType<UserSummary>();
        var toCarol = deliveries.OnlyPayload(t.Carol, ChatEvents.ProfileUpdated).ShouldBeOfType<UserSummary>();
        (toAlice.DisplayName, toAlice.IsOnline, toAlice.LastSeenUtc is null).ShouldBe(("Bobby", true, false));
        (toCarol.DisplayName, toCarol.IsOnline, toCarol.LastSeenUtc).ShouldBe(("Bobby", false, (DateTime?)null));
    }

    [Fact]
    public async Task ProfileUpdated_OfAHiddenUser_CarriesNoPresenceForAnyone()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        await SetShowLastSeenAsync(t.Bob, false);
        var mark = SignalR.Mark();

        (await SendAsync(t.Bob, new UpdateMyProfileCommand("Bobby", null))).ShouldSucceed();

        var summaries = SignalR.Since(mark).Where(d => d.Method == ChatEvents.ProfileUpdated).Select(d => (UserSummary)d.Payload).ToList();
        summaries.Count.ShouldBe(2);
        summaries.ShouldAllBe(s => !s.IsOnline && s.LastSeenUtc == null);
    }

    [Fact]
    public async Task Hiding_PushesOfflineWithoutLastSeenToSharingPeersAndHidesTheirPresenceFromTheUser()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        await GoOnlineAsync(t.Alice);
        await SetShowLastSeenAsync(t.Carol, false);
        var mark = SignalR.Mark();

        await SetShowLastSeenAsync(t.Bob, false);

        var presence = SignalR.Since(mark).Where(d => d.Method == ChatEvents.PresenceChanged).ToList();
        presence.Select(d => (d.UserId, (PresenceChangedPayload)d.Payload)).ShouldBe(
        [
            (t.Alice, PresenceChangedPayload.Hidden(t.Bob)),
            (t.Bob, PresenceChangedPayload.Hidden(t.Alice))
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task ShowingAgain_PushesTheRealStateBothWays()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        await GoOnlineAsync(t.Alice);
        await SetShowLastSeenAsync(t.Bob, false);
        var mark = SignalR.Mark();

        await SetShowLastSeenAsync(t.Bob, true);

        var states = await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == t.Alice || u.Id == t.Bob)
            .ToDictionaryAsync(u => u.Id, u => new PresenceChangedPayload(u.Id, u.IsOnline, u.LastSeenUtc)));
        var presence = SignalR.Since(mark).Where(d => d.Method == ChatEvents.PresenceChanged).ToList();
        presence.Select(d => (d.UserId, (PresenceChangedPayload)d.Payload)).ShouldBe(
        [
            (t.Alice, states[t.Bob]),
            (t.Carol, states[t.Bob]),
            (t.Bob, states[t.Alice]),
            (t.Bob, new PresenceChangedPayload(t.Carol, false, null))
        ], ignoreOrder: true);
        states[t.Bob].IsOnline.ShouldBeTrue();
    }

    [Fact]
    public async Task SettingTheSameValue_ChangesNothingAndSendsNothing()
    {
        var t = await CreateTrioAsync();
        var mark = SignalR.Mark();

        (await SendAsync(t.Bob, new UpdatePrivacySettingsCommand(true))).ShouldSucceed();

        SignalR.Since(mark).ShouldBeEmpty();
        DomainEvents.OfType<LastSeenVisibilityChangedEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Toggle_TakesEffectImmediatelyAfterEarlierCachedReads()
    {
        var t = await CreateTrioAsync();
        await GoOnlineAsync(t.Bob);
        (await SendAsync(t.Bob, new GetMeQuery())).ShouldSucceed().ShowLastSeen.ShouldBeTrue();
        (await PresenceOfBobSeenByAsync(t.Alice, t)).ShouldAllBe(v => v.IsOnline);

        await SetShowLastSeenAsync(t.Bob, false);

        (await SendAsync(t.Bob, new GetMeQuery())).ShouldSucceed().ShowLastSeen.ShouldBeFalse();
        (await PresenceOfBobSeenByAsync(t.Alice, t)).ShouldAllBe(v => !v.IsOnline && v.LastSeenUtc == null);

        await SetShowLastSeenAsync(t.Bob, true);

        (await SendAsync(t.Bob, new GetMeQuery())).ShouldSucceed().ShowLastSeen.ShouldBeTrue();
        (await PresenceOfBobSeenByAsync(t.Alice, t)).ShouldAllBe(v => v.IsOnline);
    }

    private async Task<Trio> CreateTrioAsync()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        var carol = await CreateUserAsync("Carol");
        var direct = await CreateDirectAsync(alice, bob);
        var group = await CreateGroupAsync(alice, "Trio", bob, carol);
        var bobUsername = await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == bob).Select(u => u.Username).SingleAsync());
        return new Trio(alice, bob, carol, direct, group, bobUsername.Value);
    }

    private async Task SetShowLastSeenAsync(Guid userId, bool show) =>
        (await SendAsync(userId, new UpdatePrivacySettingsCommand(show))).ShouldSucceed();

    private async Task<List<(bool IsOnline, DateTime? LastSeenUtc)>> PresenceOfBobSeenByAsync(Guid viewer, Trio t)
    {
        var views = new List<(bool, DateTime?)>();
        if (viewer == t.Alice)
        {
            var item = (await SendAsync(viewer, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Id == t.Direct);
            var other = item.OtherUser.ShouldNotBeNull();
            views.Add((other.IsOnline, other.LastSeenUtc));
        }

        var member = (await SendAsync(viewer, new GetConversationDetailsQuery(t.Group))).ShouldSucceed()
            .Members.Single(m => m.User.Id == t.Bob).User;
        views.Add((member.IsOnline, member.LastSeenUtc));

        var profile = (await SendAsync(viewer, new GetUserProfileQuery(t.Bob))).ShouldSucceed();
        views.Add((profile.IsOnline, profile.LastSeenUtc));

        var found = (await SendAsync(viewer, new SearchUsersQuery(t.BobUsername))).ShouldSucceed().Single(u => u.Id == t.Bob);
        views.Add((found.IsOnline, found.LastSeenUtc));
        return views;
    }

    private static UserSummary BlockedSummary(ChatHub.Application.Common.Models.Result<IReadOnlyList<BlockedUserResponse>> result) =>
        result.ShouldSucceed().ShouldHaveSingleItem().User;

    private sealed record Trio(Guid Alice, Guid Bob, Guid Carol, Guid Direct, Guid Group, string BobUsername);
}
