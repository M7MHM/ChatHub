using System.Text.Json;
using ChatHub.Application.Common.Emails;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Contracts;
using UserProfileUpdated = ChatHub.Contracts.Events.UserProfileUpdated;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Seeder;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class SeederTests(DatabaseFixture database) : AppTestBase(database)
{
    private static readonly SeedOptions Small = new()
    {
        Confirm = true,
        Users = 40,
        DirectChats = 20,
        Groups = 5,
        MinGroupSize = 5,
        MaxGroupSize = 12,
        Messages = 600,
        Blocks = 4,
        BatchSize = 100
    };

    [Fact]
    public async Task Seed_SmallDataset_InsertsTheRequestedRows()
    {
        var result = await SeedAsync();

        result.Skipped.ShouldBeFalse();
        var counts = await CountsAsync();
        counts.ShouldBe(new Counts(Users: 40, Directs: 20, Groups: 5, Members: result.Memberships, Messages: 600, Blocks: 4));
        result.Memberships.ShouldBeGreaterThanOrEqualTo(20 * 2 + 5 * 5);
    }

    [Fact]
    public async Task Seed_Groups_HaveOneOwnerAndOneToThreeAdmins()
    {
        await SeedAsync();

        var roles = await QueryDbAsync(db => db.Conversations
            .Where(c => c.Type == ConversationType.Group)
            .Select(c => new
            {
                Owners = c.Members.Count(m => m.Role == GroupRole.Owner),
                Admins = c.Members.Count(m => m.Role == GroupRole.Admin)
            })
            .ToListAsync());

        roles.ShouldAllBe(r => r.Owners == 1 && r.Admins >= 1 && r.Admins <= 3);
    }

    [Fact]
    public async Task Seed_EveryConversation_PointsAtItsNewestMessage()
    {
        await SeedAsync();

        var conversations = await QueryDbAsync(db => db.Conversations
            .Select(c => new
            {
                c.LastMessageId,
                c.LastMessageAtUtc,
                Newest = db.Messages
                    .Where(m => m.ConversationId == c.Id)
                    .OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id)
                    .Select(m => new { m.Id, m.CreatedAtUtc })
                    .FirstOrDefault()
            })
            .ToListAsync());

        conversations.Count.ShouldBe(25);
        conversations.ShouldAllBe(c => c.Newest == null
            ? c.LastMessageId == null && c.LastMessageAtUtc == null
            : c.LastMessageId == c.Newest.Id && c.LastMessageAtUtc == c.Newest.CreatedAtUtc);
    }

    [Fact]
    public async Task Seed_Messages_AreVisibleToMembersAndReadStateVaries()
    {
        var windowStartsAfter = DateTime.UtcNow.AddDays(-Small.Days).AddMilliseconds(-1);
        await SeedAsync();

        var hiddenFromAMember = await QueryDbAsync(db => db.Messages.CountAsync(m =>
            db.ConversationMembers.Any(c => c.ConversationId == m.ConversationId && c.JoinedAtUtc >= m.CreatedAtUtc)));
        var withReadPointer = await QueryDbAsync(db => db.ConversationMembers.CountAsync(m => m.LastReadMessageId != null));
        var withoutReadPointer = await QueryDbAsync(db => db.ConversationMembers.CountAsync(m => m.LastReadMessageId == null));
        var oldestMessage = await QueryDbAsync(db => db.Messages.MinAsync(m => m.CreatedAtUtc));

        hiddenFromAMember.ShouldBe(0);
        withReadPointer.ShouldBeGreaterThan(0);
        withoutReadPointer.ShouldBeGreaterThan(0);
        oldestMessage.ShouldBeGreaterThan(windowStartsAfter);
    }

    [Fact]
    public async Task Seed_SeededUsers_CanLogInWithTheSharedPassword()
    {
        await SeedAsync();

        var first = await LoginAsync(SeedOptions.EmailOf(1), SeedOptions.Password);
        var last = await LoginAsync(SeedOptions.EmailOf(Small.Users), SeedOptions.Password);

        first.AccessToken.ShouldNotBeNullOrWhiteSpace();
        last.User.Id.ShouldNotBe(first.User.Id);
    }

    [Fact]
    public async Task Seed_SeededUsers_GetNumberedUsernamesTheyCanSignInWith()
    {
        await SeedAsync();

        var usernames = await QueryDbAsync(db => db.UserProfiles.Select(u => u.Username).ToListAsync());
        var first = await LoginAsync("seed_0001", SeedOptions.Password);

        usernames.Select(u => u.Value).ShouldBe(Enumerable.Range(1, Small.Users).Select(SeedOptions.UsernameOf), ignoreOrder: true);
        first.User.Username.ShouldBe("seed_0001");
        (await LoginAsync(SeedOptions.EmailOf(1), SeedOptions.Password)).User.Id.ShouldBe(first.User.Id);
    }

    [Fact]
    public async Task Seed_SeededUsers_HaveAVerifiedEmailAndEmailNotificationsOn()
    {
        await SeedAsync();

        var users = await QueryDbAsync(db => db.UserProfiles.AsNoTracking().ToListAsync());
        users.Count.ShouldBe(Small.Users);
        users.ShouldAllBe(u => u.IsEmailVerified && u.EmailNotificationsEnabled && u.EmailVerifiedAtUtc == u.CreatedAtUtc);
    }

    [Fact]
    public async Task Seed_SecondRun_InsertsNothing()
    {
        await SeedAsync();
        var before = await CountsAsync();

        var second = await SeedAsync();

        second.Skipped.ShouldBeTrue();
        (await CountsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task Reset_SeedDataAndASharedChat_DeletesOnlySeedDataAndRepairsTheSharedChat()
    {
        await SeedAsync();
        var olga = await CreateUserAsync("Olga");
        var seedUser = await QueryDbAsync(db => db.UserProfiles
            .Where(u => u.Email == Email.Create(SeedOptions.EmailOf(1))).Select(u => u.Id).FirstAsync());
        var sharedChat = await CreateDirectAsync(olga, seedUser);
        var fromOlga = await SendTextAsync(olga, sharedChat, "hi");
        await SendTextAsync(seedUser, sharedChat, "hello olga");

        await using (var db = ApplicationDbContextFactory.Create(Database.ConnectionString))
            await new DataSeeder(db, Small, TextWriter.Null).ResetAsync();

        (await CountsAsync()).ShouldBe(new Counts(Users: 1, Directs: 1, Groups: 0, Members: 1, Messages: 1, Blocks: 0));
        var shared = await QueryDbAsync(db => db.Conversations.SingleAsync());
        shared.Id.ShouldBe(sharedChat);
        shared.LastMessageId.ShouldBe(fromOlga.Id);
        (await QueryDbAsync(db => db.UserProfiles.SingleAsync())).Id.ShouldBe(olga);
        (await SeedAsync()).Skipped.ShouldBeFalse();
    }

    [Fact]
    public async Task BackfillNotifications_QueuesOneUserProfileUpdatedPerLivingUserInBatches()
    {
        var anna = await CreateUserAsync("Anna");
        var ben = await CreateUserAsync("Ben", phone: "+201001234567");
        var cleo = await CreateUserAsync("Cleo");
        var dan = await CreateUserAsync("Dan");
        var gone = await CreateUserAsync("Gone");
        await UseDbAsync(async db =>
        {
            (await db.UserProfiles.SingleAsync(u => u.Id == gone)).Delete();
            await db.SaveChangesAsync();
        });
        await UseDbAsync(db => db.UserProfiles.Where(u => u.Id == anna)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAtUtc, (DateTime?)Clock.GetUtcNow().UtcDateTime)));
        await UseDbAsync(db => db.UserProfiles.Where(u => u.Id == cleo)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailNotificationsEnabled, false)));

        BackfillResult result;
        await using (var db = ApplicationDbContextFactory.Create(Database.ConnectionString))
            result = await new NotificationBackfill(db, batchSize: 2, TextWriter.Null, Clock, Links).RunAsync();

        (result.Users, result.Batches, result.UnsubscribeLinksIssued).ShouldBe((4, 2, 4));
        var rows = await QueryDbAsync(db => db.OutboxMessages.AsNoTracking().ToListAsync());
        rows.ShouldAllBe(r => r.Type == nameof(UserProfileUpdated) && r.RoutingKey == MessageRoutes.UserProfileUpdated &&
                              r.ProcessedAtUtc == null);
        var events = rows.Select(r => JsonSerializer.Deserialize<UserProfileUpdated>(r.Payload, MessageJson.Options)!).ToList();
        events.Select(e => (e.UserId, e.Email, e.DisplayName, e.EmailVerified, e.EmailNotificationsEnabled)).ShouldBe(
        [
            (anna, "anna@example.com", "Anna", true, true),
            (ben, null, "Ben", false, true),
            (cleo, "cleo@example.com", "Cleo", false, false),
            (dan, "dan@example.com", "Dan", false, true)
        ], ignoreOrder: true);
        events.ShouldAllBe(e => rows.Any(r => r.Id == e.MessageId));
        var hashes = await QueryDbAsync(db => db.UserProfiles.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.UnsubscribeTokenHash));
        foreach (var e in events)
        {
            var link = e.UnsubscribeLink.ShouldNotBeNull();
            link.ShouldStartWith("https://api.chat.example.test/api/email/unsubscribe?token=");
            OpaqueToken.Hash(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]).ShouldBe(hashes[e.UserId]);
        }
    }

    [Fact]
    public async Task BackfillNotifications_SecondRun_KeepsTheLinksUnlessAskedToRotate()
    {
        var anna = await CreateUserAsync("Anna");
        await using (var db = ApplicationDbContextFactory.Create(Database.ConnectionString))
            await new NotificationBackfill(db, batchSize: 10, TextWriter.Null, Clock, Links).RunAsync();
        var firstHash = await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == anna).Select(u => u.UnsubscribeTokenHash).SingleAsync());
        await UseDbAsync(db => db.OutboxMessages.ExecuteDeleteAsync());

        BackfillResult again;
        await using (var db = ApplicationDbContextFactory.Create(Database.ConnectionString))
            again = await new NotificationBackfill(db, batchSize: 10, TextWriter.Null, Clock, Links).RunAsync();

        again.UnsubscribeLinksIssued.ShouldBe(0);
        Deserialize(await QueryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync())).UnsubscribeLink.ShouldBeNull();
        await UseDbAsync(db => db.OutboxMessages.ExecuteDeleteAsync());

        BackfillResult rotated;
        await using (var db = ApplicationDbContextFactory.Create(Database.ConnectionString))
            rotated = await new NotificationBackfill(db, batchSize: 10, TextWriter.Null, Clock, Links, rotateUnsubscribeLinks: true).RunAsync();

        rotated.UnsubscribeLinksIssued.ShouldBe(1);
        Deserialize(await QueryDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync())).UnsubscribeLink.ShouldNotBeNull();
        (await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == anna).Select(u => u.UnsubscribeTokenHash).SingleAsync()))
            .ShouldNotBe(firstHash);
    }

    [Fact]
    public async Task VerifySeeded_MarksOnlyUnverifiedSeedUsersAsVerified()
    {
        await SeedAsync();
        await UseDbAsync(db => db.UserProfiles.ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAtUtc, (DateTime?)null)));
        var outsider = await CreateUserAsync("Olga");
        var phoneOnly = await CreateUserAsync("Pat", phone: "01001234567");

        int verified;
        await using (var db = ApplicationDbContextFactory.Create(Database.ConnectionString))
            verified = await new DataSeeder(db, Small with { VerifySeeded = true }, TextWriter.Null).VerifySeededUsersAsync();

        verified.ShouldBe(Small.Users);
        var users = await QueryDbAsync(db => db.UserProfiles.AsNoTracking().ToListAsync());
        users.Where(u => u.Id != outsider && u.Id != phoneOnly).ShouldAllBe(u => u.EmailVerifiedAtUtc == u.CreatedAtUtc);
        users.Single(u => u.Id == outsider).IsEmailVerified.ShouldBeFalse();
        users.Single(u => u.Id == phoneOnly).IsEmailVerified.ShouldBeFalse();
        SeedOptions.Parse(["--confirm", "--verify-seeded", "--backfill-notifications"]).VerifySeeded.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => SeedOptions.Parse(["--confirm", "--verify-seeded", "--reset"]));
    }

    [Fact]
    public void BackfillNotifications_CannotBeCombinedWithReset()
    {
        SeedOptions.Parse(["--confirm", "--backfill-notifications"]).BackfillNotifications.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => SeedOptions.Parse(["--confirm", "--backfill-notifications", "--reset"]))
            .Message.ShouldContain("cannot be combined with --reset");
    }

    [Fact]
    public void BackfillNotifications_ParsesThePublicUrlAndRotationAndRejectsThemOutsideABackfill()
    {
        var options = SeedOptions.Parse(["--confirm", "--backfill-notifications", "--rotate-unsubscribe-links",
            "--public-api-base-url", "https://api.chat.example.test"]);

        (options.RotateUnsubscribeLinks, options.PublicApiBaseUrl).ShouldBe((true, "https://api.chat.example.test"));
        Should.Throw<ArgumentException>(() => SeedOptions.Parse(["--confirm", "--rotate-unsubscribe-links"]));
        Should.Throw<ArgumentException>(() => SeedOptions.Parse(["--confirm", "--backfill-notifications", "--public-api-base-url", "ftp://x"]));
    }

    private static readonly EmailLinks Links = new("https://api.chat.example.test");

    private static UserProfileUpdated Deserialize(ChatHub.Infrastructure.Messaging.OutboxMessage row) =>
        JsonSerializer.Deserialize<UserProfileUpdated>(row.Payload, MessageJson.Options)!;

    private async Task<SeedResult> SeedAsync()
    {
        await using var db = ApplicationDbContextFactory.Create(Database.ConnectionString);
        return await new DataSeeder(db, Small, TextWriter.Null).SeedAsync();
    }

    private Task<Counts> CountsAsync() =>
        QueryDbAsync(async db => new Counts(
            await db.UserProfiles.CountAsync(),
            await db.Conversations.CountAsync(c => c.Type == ConversationType.Direct),
            await db.Conversations.CountAsync(c => c.Type == ConversationType.Group),
            await db.ConversationMembers.CountAsync(),
            await db.Messages.CountAsync(),
            await db.UserBlocks.CountAsync()));

    private sealed record Counts(int Users, int Directs, int Groups, int Members, int Messages, int Blocks);
}
