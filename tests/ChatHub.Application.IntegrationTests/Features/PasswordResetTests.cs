using System.Diagnostics;
using System.Text.Json;
using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Contracts;
using ChatHub.Contracts.Events;
using ChatHub.Domain.Events;
using ChatHub.Domain.ValueObjects;
using ChatHub.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class PasswordResetTests(DatabaseFixture database) : AppTestBase(database)
{
    private const string NewPassword = "brandnew789";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["Messaging:Enabled"] = "true",
        ["Messaging:User"] = "guest",
        ["Messaging:Password"] = "guest",
        ["App:FrontendBaseUrl"] = "https://chat.example.test/"
    };

    [Fact]
    public async Task ForgotPassword_VerifiedEmail_StoresOneHashedTokenAndQueuesAResetLinkToTheFrontend()
    {
        var bob = await VerifiedUserAsync("Bob", "bob@example.com");

        var response = (await SendAsync(null, new ForgotPasswordCommand("Bob@Example.com"))).ShouldSucceed();

        response.ShouldBe(ForgotPasswordResponse.Accepted);
        var requested = DomainEvents.OfType<PasswordResetRequestedEvent>().ShouldHaveSingleItem();
        var token = await QueryDbAsync(db => db.PasswordResetTokens.AsNoTracking().SingleAsync());
        (token.UserId, token.Email, token.UsedAtUtc).ShouldBe((bob, "bob@example.com", (DateTime?)null));
        token.TokenHash.ShouldBe(OpaqueToken.Hash(requested.Token));
        token.ExpiresAtUtc.ShouldBe(token.CreatedAtUtc + Lifetime);
        var published = (await PublishedAsync<PasswordResetRequested>()).ShouldHaveSingleItem();
        (published.UserId, published.Email, published.DisplayName).ShouldBe((bob, "bob@example.com", "Bob"));
        published.ResetLink.ShouldBe($"https://chat.example.test/reset-password?token={requested.Token}");
        published.ExpiresAtUtc.ShouldBe(token.ExpiresAtUtc, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ForgotPassword_UnknownUnverifiedPhoneOnlyOrDeletedAccount_AnswersTheSameAndSendsNothing()
    {
        await RegisterAsync("Una", "unverified@example.com");
        await RegisterAsync("Omar", phone: "01001234567");
        var gone = await VerifiedUserAsync("Gone", "gone@example.com");
        (await SendAsync(gone, new DeleteMyAccountCommand())).ShouldSucceed();

        var responses = new List<ForgotPasswordResponse>();
        foreach (var email in new[] { "nobody@example.com", "unverified@example.com", "gone@example.com" })
            responses.Add((await SendAsync(null, new ForgotPasswordCommand(email))).ShouldSucceed());

        responses.ShouldAllBe(r => r == ForgotPasswordResponse.Accepted);
        DomainEvents.OfType<PasswordResetRequestedEvent>().ShouldBeEmpty();
        (await QueryDbAsync(db => db.PasswordResetTokens.CountAsync())).ShouldBe(0);
        (await PublishedAsync<PasswordResetRequested>()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ForgotPassword_MalformedEmail_ThrowsValidationException()
    {
        var exception = await Should.ThrowAsync<ValidationException>(() => SendAsync(null, new ForgotPasswordCommand("not-an-email")));

        exception.Errors.Keys.ShouldBe(["Email"]);
    }

    [Fact]
    public async Task ForgotPassword_FourthRequestWithinAnHour_IsAcceptedButSendsNothingUntilTheHourPasses()
    {
        await VerifiedUserAsync("Bob", "bob@example.com");
        for (var i = 0; i < 3; i++)
        {
            (await SendAsync(null, new ForgotPasswordCommand("bob@example.com"))).ShouldSucceed();
            Clock.Advance(TimeSpan.FromMinutes(1));
        }

        (await SendAsync(null, new ForgotPasswordCommand("bob@example.com"))).ShouldSucceed().ShouldBe(ForgotPasswordResponse.Accepted);
        (await QueryDbAsync(db => db.PasswordResetTokens.CountAsync())).ShouldBe(3);

        Clock.Advance(TimeSpan.FromMinutes(58));
        (await SendAsync(null, new ForgotPasswordCommand("bob@example.com"))).ShouldSucceed();
        (await QueryDbAsync(db => db.PasswordResetTokens.CountAsync())).ShouldBe(4);
    }

    [Fact]
    public async Task ForgotPassword_NewRequest_InvalidatesTheOlderLink()
    {
        await VerifiedUserAsync("Bob", "bob@example.com");
        var older = await RequestResetAsync("bob@example.com");
        var newer = await RequestResetAsync("bob@example.com");

        (await SendAsync(null, new ResetPasswordCommand(older, NewPassword))).ShouldFailWith(AuthErrors.ResetTokenExpired);
        (await SendAsync(null, new ResetPasswordCommand(newer, NewPassword))).ShouldSucceed();
    }

    [Fact]
    public async Task ResetPassword_ValidToken_SetsThePasswordSignsOutEverywhereAndUsesTheToken()
    {
        var bob = await VerifiedUserAsync("Bob", "bob@example.com");
        var session = await LoginAsync("bob@example.com");
        var stamp = await StampOfAsync(bob);
        var token = await RequestResetAsync("bob@example.com");

        (await SendAsync(null, new ResetPasswordCommand(token, NewPassword))).ShouldSucceed();

        (await StampOfAsync(bob)).ShouldNotBe(stamp);
        (await RefreshAsync(session.RefreshToken)).ShouldFailWith(AuthErrors.InvalidRefreshToken);
        SignalR.Disconnects.ShouldBe([bob]);
        (await SendAsync(null, new LoginCommand("bob@example.com", Password))).ShouldFailWith(AuthErrors.InvalidCredentials);
        (await LoginAsync("bob@example.com", NewPassword)).User.Id.ShouldBe(bob);
        (await QueryDbAsync(db => db.PasswordResetTokens.AsNoTracking().SingleAsync())).UsedAtUtc.ShouldNotBeNull();
        var completed = (await PublishedAsync<PasswordResetCompleted>()).ShouldHaveSingleItem();
        (completed.UserId, completed.Email, completed.DisplayName).ShouldBe((bob, "bob@example.com", "Bob"));
    }

    [Fact]
    public async Task ResetPassword_SameTokenTwice_SecondTimeReportsUsed()
    {
        await VerifiedUserAsync("Bob", "bob@example.com");
        var token = await RequestResetAsync("bob@example.com");
        (await SendAsync(null, new ResetPasswordCommand(token, NewPassword))).ShouldSucceed();

        (await SendAsync(null, new ResetPasswordCommand(token, "another123"))).ShouldFailWith(AuthErrors.ResetTokenUsed);

        (await LoginAsync("bob@example.com", NewPassword)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ResetPassword_ConcurrentWithTheSameToken_SucceedsExactlyOnce()
    {
        await VerifiedUserAsync("Bob", "bob@example.com");
        var token = await RequestResetAsync("bob@example.com");

        var (a, b) = await RaceAsync(() => SendAsync(null, new ResetPasswordCommand(token, NewPassword)),
            () => SendAsync(null, new ResetPasswordCommand(token, "another123")));

        new[] { a, b }.Count(r => r.IsSuccess).ShouldBe(1);
        new[] { a, b }.Single(r => r.IsFailure).Error.ShouldBe(AuthErrors.ResetTokenUsed);
        DomainEvents.OfType<PasswordResetCompletedEvent>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ResetPassword_AfterThirtyMinutes_ReportsExpired()
    {
        await VerifiedUserAsync("Bob", "bob@example.com");
        var token = await RequestResetAsync("bob@example.com");

        Clock.Advance(Lifetime);

        (await SendAsync(null, new ResetPasswordCommand(token, NewPassword))).ShouldFailWith(AuthErrors.ResetTokenExpired);
        (await LoginAsync("bob@example.com")).ShouldNotBeNull();
    }

    [Fact]
    public async Task ResetPassword_UnknownTokenOrTokenForAnOldAddress_ReportsInvalid()
    {
        var bob = await VerifiedUserAsync("Bob", "bob@example.com");
        var token = await RequestResetAsync("bob@example.com");
        await UseDbAsync(async db =>
        {
            var user = await db.UserProfiles.SingleAsync(u => u.Id == bob);
            user.ChangeEmail(Email.Create("bob.new@example.com"));
            await db.SaveChangesAsync();
        });

        (await SendAsync(null, new ResetPasswordCommand(OpaqueToken.Generate(), NewPassword))).ShouldFailWith(AuthErrors.InvalidResetToken);
        (await SendAsync(null, new ResetPasswordCommand(token, NewPassword))).ShouldFailWith(AuthErrors.InvalidResetToken);
    }

    [Fact]
    public async Task ResetPassword_WeakPasswordOrMissingToken_ThrowsValidationException()
    {
        var exception = await Should.ThrowAsync<ValidationException>(() => SendAsync(null, new ResetPasswordCommand("", "short")));

        exception.Errors.Keys.ShouldBe(["Token", "NewPassword"], ignoreOrder: true);
    }

    [Fact]
    public async Task ResetPassword_DeletedAccount_ReportsInvalid()
    {
        var bob = await VerifiedUserAsync("Bob", "bob@example.com");
        var token = await RequestResetAsync("bob@example.com");
        (await SendAsync(bob, new DeleteMyAccountCommand())).ShouldSucceed();

        (await SendAsync(null, new ResetPasswordCommand(token, NewPassword))).ShouldFailWith(AuthErrors.InvalidResetToken);
    }

    private async Task<Guid> VerifiedUserAsync(string displayName, string email)
    {
        var user = (await RegisterAsync(displayName, email)).User.Id;
        var verification = DomainEvents.OfType<EmailVerificationRequestedEvent>().Last(e => e.UserId == user).Token;
        (await SendAsync(null, new VerifyEmailCommand(verification))).ShouldSucceed();
        return user;
    }

    private async Task<string> RequestResetAsync(string email)
    {
        var before = DomainEvents.OfType<PasswordResetRequestedEvent>().Count;
        (await SendAsync(null, new ForgotPasswordCommand(email))).ShouldSucceed();
        var events = DomainEvents.OfType<PasswordResetRequestedEvent>();
        events.Count.ShouldBe(before + 1);
        return events[^1].Token;
    }

    private async Task<List<T>> PublishedAsync<T>() where T : IIntegrationEvent =>
        (await QueryDbAsync(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == typeof(T).Name).ToListAsync()))
            .Select(row => JsonSerializer.Deserialize<T>(row.Payload, MessageJson.Options)!)
            .ToList();
}

public sealed class PasswordResetTimingTests(DatabaseFixture database) : AppTestBase(database)
{
    private static readonly TimeSpan MinimumResponseTime = TimeSpan.FromMilliseconds(500);

    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["PasswordReset:MinimumResponseMilliseconds"] = "500"
    };

    [Theory]
    [InlineData("bob@example.com")]
    [InlineData("nobody@example.com")]
    public async Task ForgotPassword_KnownOrUnknownAddress_AnswersOnlyAfterTheMinimumResponseTime(string email)
    {
        await RegisterAsync("Bob", "bob@example.com");
        await UseDbAsync(db => db.UserProfiles.ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAtUtc, DateTime.UtcNow)));
        var step = TimeSpan.FromMilliseconds(50);
        var advanced = TimeSpan.Zero;
        var stopwatch = Stopwatch.StartNew();

        var request = SendAsync(null, new ForgotPasswordCommand(email));
        while (!request.IsCompleted && stopwatch.Elapsed < Eventually.Timeout)
        {
            await Task.Delay(20);
            if (request.IsCompleted) break;
            Clock.Advance(step);
            advanced += step;
        }

        (await request).ShouldSucceed().ShouldBe(ForgotPasswordResponse.Accepted);
        advanced.ShouldBeGreaterThanOrEqualTo(MinimumResponseTime);
    }
}
