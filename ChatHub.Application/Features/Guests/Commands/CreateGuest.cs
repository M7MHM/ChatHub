using System.Security.Cryptography;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Auth;
using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.Features.Guests.Commands;

public sealed record CreateGuestCommand : IRequest<Result<AuthResponse>>;

public sealed class CreateGuestCommandHandler(
    IApplicationDbContext db,
    ISender sender,
    AuthTokenService tokens,
    IOptions<GuestSettings> options,
    TimeProvider timeProvider) : IRequestHandler<CreateGuestCommand, Result<AuthResponse>>
{
    public const string UsernamePrefix = "guest_";
    private const string UsernameAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public async Task<Result<AuthResponse>> Handle(CreateGuestCommand request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return GuestErrors.Disabled;

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var active = await db.UserProfiles.CountAsync(u => u.IsGuest && !u.IsDeleted && u.GuestExpiresAtUtc > nowUtc,
            cancellationToken);
        if (active >= settings.MaxActive) return GuestErrors.CapacityReached;

        await sender.Send(new EnsureDemoPersonasCommand(), cancellationToken);

        var username = Username.ForSystemAccount(UsernamePrefix + RandomNumberGenerator.GetString(UsernameAlphabet, 8));
        var guest = UserProfile.CreateGuest(username, $"Guest {RandomNumberGenerator.GetInt32(1000, 10000)}", nowUtc,
            settings.Lifetime);
        db.UserProfiles.Add(guest);
        GuestWorld.Seed(db, guest.Id, nowUtc);

        var response = tokens.Issue(guest);
        await db.SaveChangesAsync(cancellationToken);
        return response;
    }
}
