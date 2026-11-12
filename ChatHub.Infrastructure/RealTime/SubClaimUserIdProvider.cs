using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace ChatHub.Infrastructure.RealTime;

public class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User.FindFirst("sub")?.Value ?? connection.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
