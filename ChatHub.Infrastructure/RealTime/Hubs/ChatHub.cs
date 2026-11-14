using ChatHub.Application.Common.Interfaces;
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
        if (Context.User.GetUserId() is { } userId)
        {
            tracker.Add(userId, Context.ConnectionId);

            var conversationIds = await db.ConversationMembers
                .AsNoTracking()
                .Where(m => m.UserId == userId && m.IsActive && !m.Conversation.IsDeleted)
                .Select(m => m.ConversationId)
                .ToListAsync(Context.ConnectionAborted);

            foreach (var conversationId in conversationIds)
                await Groups.AddToGroupAsync(Context.ConnectionId, ConversationGroups.Name(conversationId), Context.ConnectionAborted);
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User.GetUserId() is { } userId)
            tracker.Remove(userId, Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
