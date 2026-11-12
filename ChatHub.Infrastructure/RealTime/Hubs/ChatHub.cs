using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime.Hubs;

[Authorize]
public class ChatHub(UserConnectionTracker tracker) : Hub
{
    public override Task OnConnectedAsync()
    {
        if (TryGetUserId(out var userId)) tracker.Add(userId, Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetUserId(out var userId)) tracker.Remove(userId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var value = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
        return Guid.TryParse(value, out userId);
    }
}
