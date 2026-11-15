using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Common.Interfaces;

namespace ChatHub.Application.Common.Extensions;

public static class CurrentUserServiceExtensions
{
    public static Guid GetRequiredUserId(this ICurrentUserService currentUser) =>
        currentUser.UserId ?? throw new NotAuthenticatedException();
}
