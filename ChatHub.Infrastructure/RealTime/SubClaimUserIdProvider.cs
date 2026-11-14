using ChatHub.Infrastructure.Identity;
using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime;

public class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.GetUserId()?.ToString();
}
