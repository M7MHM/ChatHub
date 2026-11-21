using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;
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
        await SeedAsync();

        var hiddenFromAMember = await QueryDbAsync(db => db.Messages.CountAsync(m =>
            db.ConversationMembers.Any(c => c.ConversationId == m.ConversationId && c.JoinedAtUtc >= m.CreatedAtUtc)));
        var withReadPointer = await QueryDbAsync(db => db.ConversationMembers.CountAsync(m => m.LastReadMessageId != null));
        var withoutReadPointer = await QueryDbAsync(db => db.ConversationMembers.CountAsync(m => m.LastReadMessageId == null));
        var oldestMessage = await QueryDbAsync(db => db.Messages.MinAsync(m => m.CreatedAtUtc));

        hiddenFromAMember.ShouldBe(0);
        withReadPointer.ShouldBeGreaterThan(0);
        withoutReadPointer.ShouldBeGreaterThan(0);
        oldestMessage.ShouldBeGreaterThan(DateTime.UtcNow.AddDays(-Small.Days));
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
