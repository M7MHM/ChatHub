using System.ComponentModel;
using ChatHub.Application.Common.Caching;
using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.Features.Users.Queries;

public sealed record GetMeQuery : IRequest<Result<MeResponse>>;

public sealed class GetMeQueryHandler(
    ICurrentUserService currentUser,
    HybridCache cache,
    IServiceScopeFactory scopeFactory,
    IOptions<UsernameSettings> usernameOptions) : IRequestHandler<GetMeQuery, Result<MeResponse>>
{
    public async Task<Result<MeResponse>> Handle(GetMeQuery request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();

        var me = await cache.GetOrCreateAsync(
            CacheKeys.UserProfile(userId),
            (scopeFactory, userId, cooldown: usernameOptions.Value.ChangeCooldown),
            static async (state, token) =>
            {
                await using var scope = state.scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                var row = await db.UserProfiles
                    .AsNoTracking()
                    .Where(u => u.Id == state.userId && !u.IsDeleted)
                    .Select(u => new
                    {
                        u.Id,
                        u.Username,
                        u.DisplayName,
                        u.Email,
                        EmailVerified = u.EmailVerifiedAtUtc != null,
                        u.EmailNotificationsEnabled,
                        u.PhoneNumber,
                        u.AvatarUrl,
                        u.Bio,
                        u.IsOnline,
                        u.LastSeenUtc,
                        u.ShowLastSeen,
                        u.UsernameChangedAtUtc,
                        u.CreatedAtUtc
                    })
                    .FirstOrDefaultAsync(token);

                return row is null
                    ? null
                    : new MeResponse(
                        row.Id,
                        row.Username.Value,
                        row.DisplayName,
                        row.Email?.Value,
                        row.EmailVerified,
                        row.EmailNotificationsEnabled,
                        row.PhoneNumber?.FullNumber,
                        row.AvatarUrl,
                        row.Bio,
                        row.IsOnline,
                        row.LastSeenUtc,
                        row.ShowLastSeen,
                        row.UsernameChangedAtUtc + state.cooldown,
                        row.CreatedAtUtc);
            },
            CacheKeys.UserProfileEntryOptions,
            cancellationToken: cancellationToken);

        return me is null ? UserErrors.NotFound : me;
    }
}

[ImmutableObject(true)]
public sealed record MeResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    bool EmailVerified,
    bool EmailNotificationsEnabled,
    string? PhoneNumber,
    string? AvatarUrl,
    string? Bio,
    bool IsOnline,
    DateTime? LastSeenUtc,
    bool ShowLastSeen,
    DateTime? UsernameChangeAvailableAtUtc,
    DateTime CreatedAtUtc);
