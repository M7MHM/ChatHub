using System.ComponentModel;
using ChatHub.Application.Common.Caching;
using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Application.Features.Users.Queries;

public sealed record GetMeQuery : IRequest<Result<MeResponse>>;

public sealed class GetMeQueryHandler(ICurrentUserService currentUser, HybridCache cache, IServiceScopeFactory scopeFactory)
    : IRequestHandler<GetMeQuery, Result<MeResponse>>
{
    public async Task<Result<MeResponse>> Handle(GetMeQuery request, CancellationToken cancellationToken)
    {
        var userId = currentUser.GetRequiredUserId();

        var me = await cache.GetOrCreateAsync(
            CacheKeys.UserProfile(userId),
            (scopeFactory, userId),
            static async (state, token) =>
            {
                await using var scope = state.scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
                return await db.UserProfiles
                    .AsNoTracking()
                    .Where(u => u.Id == state.userId && !u.IsDeleted)
                    .Select(u => new MeResponse(
                        u.Id,
                        u.DisplayName,
                        u.Email == null ? null : u.Email.Value,
                        u.PhoneNumber == null ? null : u.PhoneNumber.FullNumber,
                        u.AvatarUrl,
                        u.Bio,
                        u.IsOnline,
                        u.LastSeenUtc,
                        u.CreatedAtUtc))
                    .FirstOrDefaultAsync(token);
            },
            CacheKeys.UserProfileEntryOptions,
            cancellationToken: cancellationToken);

        return me is null ? UserErrors.NotFound : me;
    }
}

[ImmutableObject(true)]
public sealed record MeResponse(
    Guid Id,
    string DisplayName,
    string? Email,
    string? PhoneNumber,
    string? AvatarUrl,
    string? Bio,
    bool IsOnline,
    DateTime? LastSeenUtc,
    DateTime CreatedAtUtc);
