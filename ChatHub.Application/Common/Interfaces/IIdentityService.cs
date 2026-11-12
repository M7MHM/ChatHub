using ChatHub.Domain.Entities;

namespace ChatHub.Application.Common.Interfaces;

public interface IIdentityService
{
    string GenerateAccessToken(UserProfile user);

    string GenerateRefreshToken();
}
