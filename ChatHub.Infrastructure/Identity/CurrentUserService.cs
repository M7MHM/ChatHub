using ChatHub.Application.Common.Interfaces;
using Microsoft.AspNetCore.Http;

namespace ChatHub.Infrastructure.Identity;

public class CurrentUserService(IHttpContextAccessor httpContextAccessor) : ICurrentUserService
{
    public Guid? UserId => httpContextAccessor.HttpContext?.User.GetUserId();
}
