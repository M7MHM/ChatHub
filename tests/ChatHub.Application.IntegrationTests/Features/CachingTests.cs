using ChatHub.Application.Common.Caching;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class CachingTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task GetMe_AfterUpdateMyProfile_ReturnsTheNewProfile()
    {
        var alice = await CreateUserAsync("Alice");
        (await SendAsync(alice, new GetMeQuery())).ShouldSucceed().DisplayName.ShouldBe("Alice");

        (await SendAsync(alice, new UpdateMyProfileCommand("Alicia", "New bio"))).ShouldSucceed();

        var me = (await SendAsync(alice, new GetMeQuery())).ShouldSucceed();
        me.DisplayName.ShouldBe("Alicia");
        me.Bio.ShouldBe("New bio");
    }

    [Fact]
    public async Task GetUserProfile_ThatUserBlocksTheViewerAfterAnEarlierRead_HidesPresence()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        await GoOnlineAsync(bob);
        (await SendAsync(alice, new GetUserProfileQuery(bob))).ShouldSucceed().IsOnline.ShouldBeTrue();

        await BlockAsync(bob, alice);

        var profile = (await SendAsync(alice, new GetUserProfileQuery(bob))).ShouldSucceed();
        profile.IsOnline.ShouldBeFalse();
        profile.LastSeenUtc.ShouldBeNull();
    }

    [Fact]
    public async Task GetUserProfile_ViewerBlocksAfterAnEarlierRead_ReportsIsBlockedByMe()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        (await SendAsync(alice, new GetUserProfileQuery(bob))).ShouldSucceed().IsBlockedByMe.ShouldBeFalse();

        await BlockAsync(alice, bob);

        (await SendAsync(alice, new GetUserProfileQuery(bob))).ShouldSucceed().IsBlockedByMe.ShouldBeTrue();
    }

    [Fact]
    public async Task ConversationSoftDeleted_InvalidatesEveryMembershipEntryOfThatConversation()
    {
        var alice = await CreateUserAsync("Alice");
        var bob = await CreateUserAsync("Bob");
        var group = await CreateGroupAsync(alice, "Team", bob);
        var cache = Services.GetRequiredService<HybridCache>();
        foreach (var member in new[] { alice, bob })
        {
            (await MembershipAsync(cache, group, member, valueIfMissing: true)).ShouldBeTrue();
            (await MembershipAsync(cache, group, member, valueIfMissing: false)).ShouldBeTrue();
        }

        await UseDbAsync(async db =>
        {
            var conversation = await db.Conversations.FirstAsync(c => c.Id == group);
            db.Entry(conversation).Property(c => c.IsDeleted).CurrentValue = true;
            await db.SaveChangesAsync();
        });

        foreach (var member in new[] { alice, bob })
            (await MembershipAsync(cache, group, member, valueIfMissing: false)).ShouldBeFalse();
    }

    private static ValueTask<bool> MembershipAsync(HybridCache cache, Guid conversationId, Guid userId, bool valueIfMissing) =>
        cache.GetOrCreateAsync(
            CacheKeys.Membership(conversationId, userId),
            _ => ValueTask.FromResult(valueIfMissing),
            CacheKeys.MembershipEntryOptions,
            [CacheKeys.ConversationTag(conversationId)]);
}
