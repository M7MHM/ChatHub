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
public class ChatHub(UserConnectionTracker tracker, IApplicationDbContext db, HybridCache cache, IServiceScopeFactory scopeFactory)
    : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = GetRequiredUserId();

        var isActiveUser = await db.UserProfiles
            .AsNoTracking()
            .AnyAsync(u => u.Id == userId && !u.IsDeleted, Context.ConnectionAborted);
        if (!isActiveUser)
        {
            Context.Abort();
            return;
        }

        var isFirstConnection = tracker.Add(userId, Context);
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
                await SetPresenceAsync(userId, isOnline: true, Context.ConnectionAborted);
        }
        catch
        {
            tracker.Remove(userId, Context.ConnectionId);
            throw;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User.GetUserId() is { } userId && tracker.Remove(userId, Context.ConnectionId))
            await SetPresenceAsync(userId, isOnline: false, CancellationToken.None);

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

    private async Task SetPresenceAsync(Guid userId, bool isOnline, CancellationToken cancellationToken)
    {
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted, cancellationToken);

        if (user is null || tracker.IsOnline(userId) != isOnline) return;

        if (isOnline) user.GoOnline();
        else user.GoOffline();

        await db.SaveChangesAsync(cancellationToken);
    }
}
