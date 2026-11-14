using ChatHub.Application.Features.Users;

namespace ChatHub.Application.Features.Auth;

public sealed record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserSummary User);
