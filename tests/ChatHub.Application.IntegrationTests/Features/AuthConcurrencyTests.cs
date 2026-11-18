using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class AuthConcurrencyTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task Refresh_ConcurrentWithTheSameToken_ExactlyOneSucceeds()
    {
        var racer = await RegisterAsync("Racer", "racer@example.com");

        var (a, b) = await RaceAsync(() => RefreshAsync(racer.RefreshToken), () => RefreshAsync(racer.RefreshToken));

        new[] { a, b }.Count(r => r.IsSuccess).ShouldBe(1);
        new[] { a, b }.Single(r => r.IsFailure).Error.ShouldBe(AuthErrors.InvalidRefreshToken);
    }

    [Fact]
    public async Task Refresh_ConcurrentWithTheSameToken_LoserPairIsRolledBack()
    {
        var racer = await RegisterAsync("Racer", "racer@example.com");

        var (a, b) = await RaceAsync(() => RefreshAsync(racer.RefreshToken), () => RefreshAsync(racer.RefreshToken));

        var winner = new[] { a, b }.Single(r => r.IsSuccess).Value;
        var tokens = await QueryDbAsync(db =>
            db.RefreshTokens.AsNoTracking().Where(t => t.UserId == racer.User.Id).ToListAsync());
        tokens.Count.ShouldBe(2);
        tokens.Single(t => t.TokenHash == HashOf(racer.RefreshToken)).ReplacedByTokenHash.ShouldBe(HashOf(winner.RefreshToken));
    }

    [Fact]
    public async Task Refresh_ConcurrentWithTheSameToken_WinnerSessionStillRefreshes()
    {
        var racer = await RegisterAsync("Racer", "racer@example.com");

        var (a, b) = await RaceAsync(() => RefreshAsync(racer.RefreshToken), () => RefreshAsync(racer.RefreshToken));

        var winner = new[] { a, b }.Single(r => r.IsSuccess).Value;
        (await RefreshAsync(winner.RefreshToken)).ShouldSucceed();
    }

    [Fact]
    public async Task Refresh_ReuseSweepRacingARotation_RetriesAndReportsReuse()
    {
        var (sweepResult, midSweepRotation, _) = await RunReuseSweepWithRotationMidSweepAsync();

        sweepResult.ShouldFailWith(AuthErrors.TokenReused);
        midSweepRotation.ShouldNotBeNull().ShouldSucceed();
    }

    [Fact]
    public async Task Refresh_ReuseSweepRacingARotation_RevokesTheTokenIssuedMidSweep()
    {
        var (_, midSweepRotation, userId) = await RunReuseSweepWithRotationMidSweepAsync();

        (await RefreshAsync(midSweepRotation!.Value.RefreshToken)).IsFailure.ShouldBeTrue();
        var activeTokens = await QueryDbAsync(db =>
            db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAtUtc == null));
        activeTokens.ShouldBe(0);
    }

    [Fact]
    public async Task Register_ConcurrentWithTheSameEmail_OneSucceedsAndTheOtherGetsContactInUse()
    {
        var (a, b) = await RaceAsync(
            () => SendAsync(null, new RegisterCommand("Twin A", "twin@example.com", null, Password)),
            () => SendAsync(null, new RegisterCommand("Twin B", "TWIN@example.com", null, Password)));

        new[] { a, b }.Count(r => r.IsSuccess).ShouldBe(1);
        new[] { a, b }.Single(r => r.IsFailure).Error.ShouldBe(AuthErrors.ContactInUse);
    }

    [Fact]
    public async Task Register_ConcurrentWithTheSamePhone_OneSucceedsAndTheOtherGetsContactInUse()
    {
        var (a, b) = await RaceAsync(
            () => SendAsync(null, new RegisterCommand("Phone A", null, "+20 111 222 3333", Password)),
            () => SendAsync(null, new RegisterCommand("Phone B", null, "+201112223333", Password)));

        new[] { a, b }.Count(r => r.IsSuccess).ShouldBe(1);
        new[] { a, b }.Single(r => r.IsFailure).Error.ShouldBe(AuthErrors.ContactInUse);
    }

    [Fact]
    public async Task Register_ConcurrentWithTheSameContact_StoresOnlyOneUserPerContact()
    {
        await RaceAsync(
            () => SendAsync(null, new RegisterCommand("Twin A", "twin@example.com", null, Password)),
            () => SendAsync(null, new RegisterCommand("Twin B", "TWIN@example.com", null, Password)));
        await RaceAsync(
            () => SendAsync(null, new RegisterCommand("Phone A", null, "+20 111 222 3333", Password)),
            () => SendAsync(null, new RegisterCommand("Phone B", null, "+201112223333", Password)));

        var email = Email.Create("twin@example.com");
        var phone = PhoneNumber.Create("+201112223333");
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.Email == email))).ShouldBe(1);
        (await QueryDbAsync(db => db.UserProfiles.CountAsync(u => u.PhoneNumber == phone))).ShouldBe(1);
    }

    private string HashOf(string refreshToken) => Services.GetRequiredService<IIdentityService>().HashToken(refreshToken);

    private async Task<(Result<AuthResponse> Sweep, Result<AuthResponse>? MidSweepRotation, Guid UserId)>
        RunReuseSweepWithRotationMidSweepAsync()
    {
        var sweeper = await RegisterAsync("Sweeper", "sweeper@example.com");
        var second = (await RefreshAsync(sweeper.RefreshToken)).ShouldSucceed();
        Result<AuthResponse>? midSweepRotation = null;

        var sweepResult = await Task.Run(async () =>
        {
            // Call 1 is the sweep's "now"; call 2 runs inside its SaveChanges, after it loaded the active tokens.
            Clock.OnCall(call =>
            {
                if (call != 2) return;
                Task<Result<AuthResponse>> rotation;
                using (ExecutionContext.SuppressFlow())
                    rotation = Task.Run(() => RefreshAsync(second.RefreshToken));
                midSweepRotation = rotation.GetAwaiter().GetResult();
            });
            return await RefreshAsync(sweeper.RefreshToken);
        });

        return (sweepResult, midSweepRotation, sweeper.User.Id);
    }
}
