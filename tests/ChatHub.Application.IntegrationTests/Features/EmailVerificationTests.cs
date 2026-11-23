using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Users.Queries;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Events;
using ChatHub.Domain.ValueObjects;
using ChatHub.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class EmailVerificationTests(DatabaseFixture database) : AppTestBase(database)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    [Fact]
    public async Task Register_WithEmail_StoresOneHashedTokenAndRequestsVerification()
    {
        var bob = await RegisterAsync("Bob", "Bob@Example.com");

        var requested = DomainEvents.OfType<EmailVerificationRequestedEvent>().ShouldHaveSingleItem();
        var token = await QueryDbAsync(db => db.EmailVerificationTokens.AsNoTracking().SingleAsync());
        (token.UserId, token.Email, token.UsedAtUtc).ShouldBe((bob.User.Id, "bob@example.com", null));
        token.TokenHash.ShouldBe(OpaqueToken.Hash(requested.Token));
        token.TokenHash.ShouldNotContain(requested.Token);
        requested.Token.Length.ShouldBe(43);
        token.ExpiresAtUtc.ShouldBe(token.CreatedAtUtc + Lifetime);
        token.CreatedAtUtc.ShouldBe(Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMilliseconds(1));

        var me = (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed();
        (me.EmailVerified, me.EmailNotificationsEnabled).ShouldBe((false, true));
    }

    [Fact]
    public async Task Register_PhoneOnly_RequestsNoVerificationButIssuesAnUnsubscribeToken()
    {
        var omar = await RegisterAsync("Omar", phone: "01001234567");

        DomainEvents.OfType<EmailVerificationRequestedEvent>().ShouldBeEmpty();
        (await QueryDbAsync(db => db.EmailVerificationTokens.CountAsync())).ShouldBe(0);
        var issued = DomainEvents.OfType<UnsubscribeTokenIssuedEvent>().ShouldHaveSingleItem();
        (await QueryDbAsync(db => db.UserProfiles.Where(u => u.Id == omar.User.Id).Select(u => u.UnsubscribeTokenHash).SingleAsync()))
            .ShouldBe(OpaqueToken.Hash(issued.Token));
    }

    [Fact]
    public async Task VerifyEmail_ValidToken_VerifiesTheAddressAndGetMeShowsIt()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed().EmailVerified.ShouldBeFalse();

        (await SendAsync(null, new VerifyEmailCommand(LastToken()))).ShouldSucceed();

        (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed().EmailVerified.ShouldBeTrue();
        var token = await QueryDbAsync(db => db.EmailVerificationTokens.AsNoTracking().SingleAsync());
        token.UsedAtUtc.ShouldNotBeNull();
        DomainEvents.OfType<EmailVerifiedEvent>().ShouldHaveSingleItem().UserId.ShouldBe(bob.User.Id);
    }

    [Fact]
    public async Task VerifyEmail_SameTokenTwice_SecondTimeReportsAlreadyVerified()
    {
        await RegisterAsync("Bob", "bob@example.com");
        var token = LastToken();
        (await SendAsync(null, new VerifyEmailCommand(token))).ShouldSucceed();

        (await SendAsync(null, new VerifyEmailCommand(token))).ShouldFailWith(AuthErrors.EmailAlreadyVerified);

        DomainEvents.OfType<EmailVerifiedEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task VerifyEmail_ConcurrentWithTheSameToken_VerifiesExactlyOnce()
    {
        await RegisterAsync("Bob", "bob@example.com");
        var token = LastToken();

        var (a, b) = await RaceAsync(() => SendAsync(null, new VerifyEmailCommand(token)),
            () => SendAsync(null, new VerifyEmailCommand(token)));

        new[] { a, b }.Count(r => r.IsSuccess).ShouldBe(1);
        new[] { a, b }.Single(r => r.IsFailure).Error.ShouldBe(AuthErrors.EmailAlreadyVerified);
        DomainEvents.OfType<EmailVerifiedEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task VerifyEmail_After24Hours_ReportsExpired()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        Clock.Advance(Lifetime);

        (await SendAsync(null, new VerifyEmailCommand(LastToken()))).ShouldFailWith(AuthErrors.VerificationTokenExpired);

        (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed().EmailVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyEmail_UnknownToken_ReportsInvalid()
    {
        await RegisterAsync("Bob", "bob@example.com");

        (await SendAsync(null, new VerifyEmailCommand(OpaqueToken.Generate()))).ShouldFailWith(AuthErrors.InvalidVerificationToken);
    }

    [Fact]
    public async Task VerifyEmail_TokenForAPreviousAddress_IsInvalidAndTheNewAddressStaysUnverified()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        await UseDbAsync(async db =>
        {
            (await db.UserProfiles.SingleAsync(u => u.Id == bob.User.Id)).ChangeEmail(Email.Create("robert@example.com"));
            await db.SaveChangesAsync();
        });

        (await SendAsync(null, new VerifyEmailCommand(LastToken()))).ShouldFailWith(AuthErrors.InvalidVerificationToken);

        var me = (await SendAsync(bob.User.Id, new GetMeQuery())).ShouldSucceed();
        (me.Email, me.EmailVerified).ShouldBe(("robert@example.com", false));
    }

    [Fact]
    public async Task VerifyEmail_UsedTokenAfterTheAddressWasVerifiedAgainElsewhere_ReportsUsed()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        var first = LastToken();
        (await SendAsync(null, new VerifyEmailCommand(first))).ShouldSucceed();
        await UseDbAsync(db => db.UserProfiles.Where(u => u.Id == bob.User.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAtUtc, (DateTime?)null)));

        (await SendAsync(null, new VerifyEmailCommand(first))).ShouldFailWith(AuthErrors.VerificationTokenUsed);
    }

    [Fact]
    public async Task RequestEmailVerification_InvalidatesTheOlderUnusedToken()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        var first = LastToken();

        var response = (await SendAsync(bob.User.Id, new RequestEmailVerificationCommand())).ShouldSucceed();
        var second = LastToken();

        second.ShouldNotBe(first);
        response.Email.ShouldBe("bob@example.com");
        response.ExpiresAtUtc.ShouldBe(Clock.GetUtcNow().UtcDateTime + Lifetime, TimeSpan.FromMilliseconds(1));
        (await SendAsync(null, new VerifyEmailCommand(first))).ShouldFailWith(AuthErrors.VerificationTokenExpired);
        (await SendAsync(null, new VerifyEmailCommand(second))).ShouldSucceed();
    }

    [Fact]
    public async Task RequestEmailVerification_ThreePerHourIncludingRegistration_ThenThrottledUntilTheOldestLeavesTheWindow()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        Clock.Advance(TimeSpan.FromMinutes(10));
        (await SendAsync(bob.User.Id, new RequestEmailVerificationCommand())).ShouldSucceed();
        (await SendAsync(bob.User.Id, new RequestEmailVerificationCommand())).ShouldSucceed();

        (await SendAsync(bob.User.Id, new RequestEmailVerificationCommand())).ShouldFailWith(AuthErrors.VerificationRequestsThrottled);
        (await QueryDbAsync(db => db.EmailVerificationTokens.CountAsync())).ShouldBe(3);

        Clock.Advance(TimeSpan.FromMinutes(50));
        (await SendAsync(bob.User.Id, new RequestEmailVerificationCommand())).ShouldSucceed();
        DomainEvents.OfType<EmailVerificationRequestedEvent>().Count.ShouldBe(4);
    }

    [Fact]
    public async Task RequestEmailVerification_WithoutAnEmail_ReportsNoEmailToVerify()
    {
        var omar = await RegisterAsync("Omar", phone: "01001234567");

        (await SendAsync(omar.User.Id, new RequestEmailVerificationCommand())).ShouldFailWith(AuthErrors.NoEmailToVerify);
    }

    [Fact]
    public async Task RequestEmailVerification_WhenAlreadyVerified_ReportsAlreadyVerified()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");
        (await SendAsync(null, new VerifyEmailCommand(LastToken()))).ShouldSucceed();

        (await SendAsync(bob.User.Id, new RequestEmailVerificationCommand())).ShouldFailWith(AuthErrors.EmailAlreadyVerified);
    }

    private string LastToken() => DomainEvents.OfType<EmailVerificationRequestedEvent>()[^1].Token;
}
