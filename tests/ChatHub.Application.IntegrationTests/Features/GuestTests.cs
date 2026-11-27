using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Conversations.Commands;
using ChatHub.Application.Features.Conversations.Queries;
using ChatHub.Application.Features.Guests;
using ChatHub.Application.Features.Guests.Commands;
using ChatHub.Application.Features.Messages.Queries;
using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using ChatHub.Infrastructure.Guests;
using ChatHub.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class GuestTests(DatabaseFixture database) : AppTestBase(database)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    [Fact]
    public async Task CreateGuest_ReturnsATokenPairForAGuestThatExpiresInADay()
    {
        var auth = await CreateGuestAsync();

        auth.AccessToken.Length.ShouldBeGreaterThan(100);
        auth.User.Username.ShouldNotBeNull().ShouldStartWith("guest_");
        auth.User.DisplayName.ShouldMatch(@"^Guest \d{4}$");
        var me = (await SendAsync(auth.User.Id, new GetMeQuery())).ShouldSucceed();
        (me.IsGuest, me.Email, me.PhoneNumber).ShouldBe((true, (string?)null, (string?)null));
        me.GuestExpiresAtUtc.ShouldNotBeNull().ShouldBe(Clock.GetUtcNow().UtcDateTime + Lifetime, TimeSpan.FromMilliseconds(1));
        DomainEvents.OfType<UserRegisteredEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateGuest_CreatesTheDemoPersonasOnceAndKeepsThemUnableToSignIn()
    {
        await CreateGuestAsync();
        await CreateGuestAsync();

        var personas = await QueryDbAsync(db => db.UserProfiles.Where(u => u.IsDemoPersona).Select(u => u.Username).ToListAsync());
        personas.Select(u => u.Value).ShouldBe(DemoPersonas.All.Select(p => p.Username), ignoreOrder: true);
        (await SendAsync(null, new EnsureDemoPersonasCommand())).ShouldSucceed().ShouldBe(0);
        (await SendAsync(null, new LoginCommand("demo_layla", Password))).ShouldFailWith(AuthErrors.InvalidCredentials);
        (await SendAsync(null, new LoginCommand("chathub_bot", "!"))).ShouldFailWith(AuthErrors.InvalidCredentials);
    }

    [Fact]
    public async Task EnsureDemoPersonas_TwoInstancesAtOnce_CreateEachPersonaOnce()
    {
        var results = await Task.WhenAll(SendAsync(null, new EnsureDemoPersonasCommand()),
            SendAsync(null, new EnsureDemoPersonasCommand()));

        results.Sum(r => r.ShouldSucceed()).ShouldBe(DemoPersonas.All.Count);
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.IsDemoPersona))).ShouldBe(DemoPersonas.All.Count);
    }

    [Fact]
    public async Task CreateGuest_SeedsThreeConversationsWithHistoryAndUnreadBadges()
    {
        var guest = (await CreateGuestAsync()).User.Id;

        var list = (await SendAsync(guest, new GetMyConversationsQuery())).ShouldSucceed().Items;

        list.Count.ShouldBe(3);
        var bot = list.Single(i => i.OtherUser?.Id == DemoPersonas.Bot.Id);
        var layla = list.Single(i => i.OtherUser?.Id == DemoPersonas.Layla.Id);
        var group = list.Single(i => i.Type == ConversationType.Group);
        (bot.Title, bot.UnreadCount, bot.LastMessage!.SenderId).ShouldBe(("ChatHub Bot", 2, DemoPersonas.Bot.Id));
        (layla.UnreadCount, group.Title, group.UnreadCount).ShouldBe((2, GuestWorld.GroupTitle, 3));
        list.ShouldAllBe(i => i.LastMessageAtUtc < Clock.GetUtcNow().UtcDateTime);

        var botMessages = (await SendAsync(guest, new GetMessagesQuery { ConversationId = bot.Id })).ShouldSucceed().Items;
        var groupMessages = (await SendAsync(guest, new GetMessagesQuery { ConversationId = group.Id })).ShouldSucceed().Items;
        botMessages.Count.ShouldBe(4);
        groupMessages.Count.ShouldBe(6);
        groupMessages.Select(m => m.Sender.Id).Distinct().ShouldBe(
            [DemoPersonas.Omar.Id, DemoPersonas.Sara.Id, DemoPersonas.Youssef.Id], ignoreOrder: true);
        var details = (await SendAsync(guest, new GetConversationDetailsQuery(group.Id))).ShouldSucceed();
        (details.MyRole, details.Members.Count).ShouldBe((GroupRole.Member, 4));
    }

    [Fact]
    public async Task CreateGuest_WritesItsWorldInOneSaveWithoutLiveEvents()
    {
        await CreateGuestAsync();
        Logs.ResetSqlCommands();
        var signalMark = SignalR.Mark();

        await CreateGuestAsync();

        Logs.SqlCommands.ShouldBeLessThanOrEqualTo(4);
        SignalR.Since(signalMark).ShouldBeEmpty();
        DomainEvents.OfType<MessageSentEvent>().ShouldBeEmpty();
        DomainEvents.OfType<ConversationCreatedEvent>().ShouldBeEmpty();
        DomainEvents.OfType<MemberAddedEvent>().ShouldBeEmpty();
        DomainEvents.OfType<MessageReadEvent>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Guest_CanOnlyStartChatsAndGroupsWithPersonas()
    {
        var guest = (await CreateGuestAsync()).User.Id;
        var alice = await CreateUserAsync("Alice");

        (await SendAsync(guest, new CreateDirectConversationCommand(alice))).ShouldFailWith(GuestErrors.ContactRestricted);
        (await SendAsync(guest, new CreateGroupCommand("Mixed", null, [DemoPersonas.Omar.Id, alice])))
            .ShouldFailWith(GuestErrors.ContactRestricted);
        (await SendAsync(guest, new CreateDirectConversationCommand(DemoPersonas.Omar.Id))).ShouldSucceed().Created.ShouldBeTrue();
        var own = (await SendAsync(guest, new CreateGroupCommand("Mine", null, [DemoPersonas.Sara.Id]))).ShouldSucceed().ConversationId;
        (await SendAsync(guest, new AddMemberCommand(alice) { ConversationId = own })).ShouldFailWith(GuestErrors.ContactRestricted);
        (await SendAsync(guest, new AddMemberCommand(DemoPersonas.Bot.Id) { ConversationId = own })).ShouldSucceed();
    }

    [Fact]
    public async Task Guest_CannotStartAChatWithAnotherGuest()
    {
        var first = (await CreateGuestAsync()).User.Id;
        var second = (await CreateGuestAsync()).User.Id;

        (await SendAsync(first, new CreateDirectConversationCommand(second))).ShouldFailWith(GuestErrors.ContactRestricted);
    }

    [Fact]
    public async Task RealUsers_CannotStartChatsWithAddOrLookUpGuests()
    {
        var guest = (await CreateGuestAsync()).User.Id;
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        var group = await CreateGroupAsync(alice, "Team", bob);

        (await SendAsync(alice, new CreateDirectConversationCommand(guest))).ShouldFailWith(GuestErrors.NotReachable);
        (await SendAsync(alice, new CreateGroupCommand("With guest", null, [bob, guest]))).ShouldFailWith(GuestErrors.NotReachable);
        (await SendAsync(alice, new AddMemberCommand(guest) { ConversationId = group })).ShouldFailWith(GuestErrors.NotReachable);
        (await SendAsync(alice, new GetUserProfileQuery(guest))).ShouldFailWith(UserErrors.NotFound);
        (await SendAsync(guest, new GetUserProfileQuery(alice))).ShouldFailWith(UserErrors.NotFound);
        (await SendAsync(guest, new GetUserProfileQuery(DemoPersonas.Layla.Id))).ShouldSucceed().Username.ShouldBe("demo_layla");
        (await SendAsync(alice, new CreateDirectConversationCommand(DemoPersonas.Bot.Id))).ShouldSucceed();
    }

    [Fact]
    public async Task Search_GuestsFindOnlyPersonasAndRealUsersNeverFindGuests()
    {
        var guestAuth = await CreateGuestAsync();
        var guest = guestAuth.User.Id;
        var alice = await CreateUserAsync("Alice Layla");

        var guestResults = (await SendAsync(guest, new SearchUsersQuery("la"))).ShouldSucceed();
        var byUsername = (await SendAsync(alice, new SearchUsersQuery(guestAuth.User.Username!))).ShouldSucceed();
        var byName = (await SendAsync(alice, new SearchUsersQuery(guestAuth.User.DisplayName))).ShouldSucceed();
        var aliceFindsPersonas = (await SendAsync(alice, new SearchUsersQuery("layla"))).ShouldSucceed();

        guestResults.Select(u => u.Id).ShouldBe([DemoPersonas.Layla.Id]);
        (await SendAsync(guest, new SearchUsersQuery("alice"))).ShouldSucceed().ShouldBeEmpty();
        byUsername.ShouldBeEmpty();
        byName.ShouldBeEmpty();
        aliceFindsPersonas.Select(u => u.Id).ShouldBe([DemoPersonas.Layla.Id]);
    }

    [Fact]
    public async Task Guest_CannotChangeUsernameRequestVerificationOrChangePassword()
    {
        var guest = (await CreateGuestAsync()).User.Id;

        (await SendAsync(guest, new ChangeUsernameCommand("my_name"))).ShouldFailWith(GuestErrors.AccountRestricted);
        (await SendAsync(guest, new RequestEmailVerificationCommand())).ShouldFailWith(GuestErrors.AccountRestricted);
        (await SendAsync(guest, new ChangePasswordCommand("anything1", "newsecret1"))).ShouldFailWith(GuestErrors.AccountRestricted);
        (await SendAsync(guest, new UpdateMyProfileCommand("Curious Guest", null))).ShouldSucceed();
    }

    [Fact]
    public async Task Guest_CannotSignInWithItsUsernameAndGetsNoResetEmail()
    {
        var auth = await CreateGuestAsync();

        (await SendAsync(null, new LoginCommand(auth.User.Username!, "whatever1"))).ShouldFailWith(AuthErrors.InvalidCredentials);
        (await QueryDbAsync(db => db.PasswordResetTokens.CountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task Refresh_AfterTheGuestExpired_IsRejectedEvenBeforeTheCleanup()
    {
        var auth = await CreateGuestAsync();
        var rotated = (await RefreshAsync(auth.RefreshToken)).ShouldSucceed();

        Clock.Advance(Lifetime);

        (await RefreshAsync(rotated.RefreshToken)).ShouldFailWith(AuthErrors.InvalidRefreshToken);
        (await QueryDbAsync(db => db.UserProfiles.SingleAsync(u => u.Id == auth.User.Id))).IsDeleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyExpiredGuestsLikeAnAccountDeletionAndRemovesTheirConversations()
    {
        var expiring = (await CreateGuestAsync()).User.Id;
        Clock.Advance(TimeSpan.FromHours(1));
        var fresh = (await CreateGuestAsync()).User.Id;
        var expiringConversations = await ConversationIdsOfAsync(expiring);
        Clock.Advance(Lifetime - TimeSpan.FromMinutes(30));

        var deleted = await Cleanup.RunOnceAsync(CancellationToken.None);

        deleted.ShouldBe(1);
        var users = await QueryDbAsync(db => db.UserProfiles.Where(u => u.IsGuest).ToDictionaryAsync(u => u.Id, u => u.IsDeleted));
        (users[expiring], users[fresh]).ShouldBe((true, false));
        DomainEvents.OfType<UserDeletedEvent>().ShouldHaveSingleItem().UserId.ShouldBe(expiring);
        SignalR.Disconnects.ShouldBe([expiring]);
        (await QueryDbAsync(db => db.Conversations.CountAsync(c => expiringConversations.Contains(c.Id)))).ShouldBe(0);
        (await QueryDbAsync(db => db.Messages.CountAsync(m => expiringConversations.Contains(m.ConversationId)))).ShouldBe(0);
        (await ConversationIdsOfAsync(fresh)).Count.ShouldBe(3);
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.IsDemoPersona && !u.IsDeleted))).ShouldBe(DemoPersonas.All.Count);
        (await Cleanup.RunOnceAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task Cleanup_KeepsAConversationThatARealUserIsIn()
    {
        var guest = (await CreateGuestAsync()).User.Id;
        var alice = await CreateUserAsync("Alice");
        var group = (await SendAsync(guest, new GetMyConversationsQuery())).ShouldSucceed().Items.Single(i => i.Type == ConversationType.Group).Id;
        await UseDbAsync(async db =>
        {
            var conversation = await db.Conversations.Include(c => c.Members).SingleAsync(c => c.Id == group);
            conversation.AddMember(DemoPersonas.Omar.Id, alice);
            await db.SaveChangesAsync();
        });
        Clock.Advance(Lifetime);

        await Cleanup.RunOnceAsync(CancellationToken.None);

        var remaining = await QueryDbAsync(db => db.ConversationMembers.Where(m => m.UserId == guest).Select(m => m.ConversationId).ToListAsync());
        remaining.ShouldBe([group]);
        (await QueryDbAsync(db => db.ConversationMembers.SingleAsync(m => m.UserId == guest))).IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Cleanup_MarksTheGuestsUploadsDeleted()
    {
        var guest = (await CreateGuestAsync()).User.Id;
        var botChat = (await SendAsync(guest, new GetMyConversationsQuery())).ShouldSucceed().Items
            .Single(i => i.OtherUser?.Id == DemoPersonas.Bot.Id).Id;
        var image = await UploadPngAsync(guest);
        await SendFileAsync(guest, botChat, MessageType.Image, image.Id);
        var loose = await UploadPngAsync(guest);
        Clock.Advance(Lifetime);

        await Cleanup.RunOnceAsync(CancellationToken.None);

        var files = await QueryDbAsync(db => db.UploadedFiles.Where(f => f.OwnerId == guest).Select(f => new { f.Id, f.IsDeleted }).ToListAsync());
        files.Select(f => f.Id).ShouldBe([image.Id, loose.Id], ignoreOrder: true);
        files.ShouldAllBe(f => f.IsDeleted);
    }

    [Fact]
    public async Task Cleanup_TwoRunnersAtOnce_DeleteEachExpiredGuestExactlyOnce()
    {
        var guests = new List<Guid>();
        for (var i = 0; i < 4; i++) guests.Add((await CreateGuestAsync()).User.Id);
        Clock.Advance(Lifetime);
        var second = new GuestCleanupService(Services.GetRequiredService<IServiceScopeFactory>(),
            Services.GetRequiredService<IDistributedLock>(), Services.GetRequiredService<IOptions<GuestSettings>>(), Clock,
            NullLogger<GuestCleanupService>.Instance);

        var runs = await Task.WhenAll(Cleanup.RunOnceAsync(CancellationToken.None), second.RunOnceAsync(CancellationToken.None));

        runs.Sum(r => r ?? 0).ShouldBe(4);
        DomainEvents.OfType<UserDeletedEvent>().Select(e => e.UserId).ShouldBe(guests, ignoreOrder: true);
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.IsGuest && !u.IsDeleted))).ShouldBe(0);
    }

    [Fact]
    public async Task Cleanup_WhileAnotherInstanceHoldsTheLock_SkipsTheCycle()
    {
        await CreateGuestAsync();
        Clock.Advance(Lifetime);
        await using var held = await Services.GetRequiredService<IDistributedLock>()
            .TryAcquireAsync(GuestCleanupService.LockName, TimeSpan.Zero, CancellationToken.None);
        held.ShouldNotBeNull();

        (await Cleanup.RunOnceAsync(CancellationToken.None)).ShouldBeNull();
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.IsGuest && !u.IsDeleted))).ShouldBe(1);
    }

    [Fact]
    public async Task DeleteMyAccount_AsAGuest_EndsTheSessionEarly()
    {
        var guest = (await CreateGuestAsync()).User.Id;

        (await SendAsync(guest, new DeleteMyAccountCommand())).ShouldSucceed();

        (await QueryDbAsync(db => db.UserProfiles.SingleAsync(u => u.Id == guest))).IsDeleted.ShouldBeTrue();
        (await SendAsync(null, new DeleteExpiredGuestCommand(guest))).ShouldSucceed().ShouldBeFalse();
    }

    private GuestCleanupService Cleanup => Services.GetRequiredService<GuestCleanupService>();

    private async Task<AuthResponse> CreateGuestAsync() => (await SendAsync(null, new CreateGuestCommand())).ShouldSucceed();

    private Task<List<Guid>> ConversationIdsOfAsync(Guid userId) =>
        QueryDbAsync(db => db.ConversationMembers.Where(m => m.UserId == userId).Select(m => m.ConversationId).ToListAsync());
}

public sealed class GuestCapacityTests(DatabaseFixture database) : AppTestBase(database)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Guest:MaxActive"] = "2"
    };

    [Fact]
    public async Task CreateGuest_AtTheCap_ReturnsCapacityReachedUntilAGuestExpires()
    {
        (await SendAsync(null, new CreateGuestCommand())).ShouldSucceed();
        Clock.Advance(TimeSpan.FromHours(1));
        (await SendAsync(null, new CreateGuestCommand())).ShouldSucceed();

        (await SendAsync(null, new CreateGuestCommand())).ShouldFailWith(GuestErrors.CapacityReached);

        Clock.Advance(TimeSpan.FromHours(23));
        (await SendAsync(null, new CreateGuestCommand())).ShouldSucceed();
    }
}

public sealed class GuestDisabledTests(DatabaseFixture database) : AppTestBase(database)
{
    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Guest:Enabled"] = "false"
    };

    [Fact]
    public async Task CreateGuest_WhenGuestModeIsOff_ReturnsDisabledAndCreatesNothing()
    {
        (await SendAsync(null, new CreateGuestCommand())).ShouldFailWith(GuestErrors.Disabled);

        (await QueryDbAsync(db => db.UserProfiles.CountAsync())).ShouldBe(0);
    }
}
