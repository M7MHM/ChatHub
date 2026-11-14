using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.RealTime;
using ChatHub.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Infrastructure.RealTime.Hubs;

[Authorize]
public class ChatHub(UserConnectionTracker tracker, IApplicationDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = GetRequiredUserId();

        // Track first so group changes made while we are still joining reach this connection.
        var isFirstConnection = tracker.Add(userId, Context.ConnectionId);
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
            // SignalR does not call OnDisconnectedAsync when OnConnectedAsync fails, so undo the tracking here.
            tracker.Remove(userId, Context.ConnectionId);
            throw;
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // ConnectionAborted is already cancelled at this point, so the presence update must not use it.
        if (Context.User.GetUserId() is { } userId && tracker.Remove(userId, Context.ConnectionId))
            await SetPresenceAsync(userId, isOnline: false, CancellationToken.None);

        await base.OnDisconnectedAsync(exception);
    }

    public async Task Typing(Guid conversationId)
    {
        if (conversationId == Guid.Empty)
            throw new HubException("A conversation id is required.");

        var userId = GetRequiredUserId();
        var isActiveMember = await db.ConversationMembers
            .AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId &&
                           m.IsActive && !m.Conversation.IsDeleted, Context.ConnectionAborted);
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

        // Another connection may have changed the state while the user was loading; the tracker is the source of truth.
        if (user is null || tracker.IsOnline(userId) != isOnline) return;

        if (isOnline) user.GoOnline();
        else user.GoOffline();

        await db.SaveChangesAsync(cancellationToken);
    }
}
