using ChatHub.Application.Common.Caching;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Infrastructure.RealTime.Hubs;

[Authorize]
public class ChatHub(
    IUserConnectionTracker tracker,
    PresenceSynchronizer presence,
    IApplicationDbContext db,
    HybridCache cache,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = GetRequiredUserId();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var isActiveUser = await db.UserProfiles
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && !u.IsDeleted && (!u.IsGuest || u.GuestExpiresAtUtc > nowUtc), Context.ConnectionAborted);
        if (!isActiveUser)
        {
            Context.Abort();
            return;
        }

        var isFirstConnection = await tracker.AddAsync(userId, Context, Context.ConnectionAborted);
        try
        {
            var conversationIds = await db.ConversationMembers
                .AsNoTracking()
                .Where(m => m.UserId == userId && m.IsActive && !m.Conversation.IsDeleted)
                .Select(m => m.ConversationId)
                .ToListAsync(Context.ConnectionAborted);

            foreach (var conversationId in conversationIds)
                await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroups.Name(conversationId), Context.ConnectionAborted);

            if (isFirstConnection)
                await presence.SyncAsync(userId, CancellationToken.None);
        }
        catch
        {
            if (await tracker.RemoveAsync(userId, Context.ConnectionId, CancellationToken.None))
                await presence.SyncAsync(userId, CancellationToken.None);
            throw;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User.GetUserId() is { } userId && await tracker.RemoveAsync(userId, Context.ConnectionId, CancellationToken.None))
            await presence.SyncAsync(userId, CancellationToken.None);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task Typing(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
            throw new HubException("A conversation id is required.");

        var userId = GetRequiredUserId();
        var isActiveMember = await cache.GetOrCreateAsync(
            CacheKeys.Membership(conversationId, userId),
            (scopeFactory, conversationId, userId),
            static async (state, token) =>
            {
                await using var scope = state.scopeFactory.CreateAsyncScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                return await scopedDb.ConversationMembers
                    .AsNoTracking()
                    .AnyAsync(m => m.ConversationId == state.conversationId && m.UserId == state.userId &&
                                   m.IsActive && !m.Conversation.IsDeleted, token);
            },
            CacheKeys.MembershipEntryOptions,
            [CacheKeys.ConversationTag(conversationId)],
            Context.ConnectionAborted);
        if (!isActiveMember)
            throw new HubException("You are not an active member of this conversation.");

        await Clients.OthersInGroup(ConversationGroups.Name(conversationId))
            .SendAsync(ChatEvents.UserTyping, new { conversationId, userId }, Context.ConnectionAborted);
    }

    private Guid GetRequiredUserId() =>
        Context.User.GetUserId() ?? throw new HubException("The access token does not contain a valid user id.");
}
