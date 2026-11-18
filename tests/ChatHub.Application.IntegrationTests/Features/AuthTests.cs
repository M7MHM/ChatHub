using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Auth.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class AuthTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task Register_ValidRequest_ReturnsTokenPair()
    {
        var response = await RegisterAsync("Alice", "Alice@Example.com");

        response.AccessToken.Length.ShouldBeGreaterThan(100);
        response.RefreshToken.Length.ShouldBe(86);
    }

    [Fact]
    public async Task Register_DuplicateEmailInAnotherCase_ReturnsContactInUse()
    {
        await RegisterAsync("Alice", "Alice@Example.com");

        var result = await SendAsync(null, new RegisterCommand("X", "alice@example.com", null, Password));

        result.ShouldFailWith(AuthErrors.ContactInUse);
    }

    [Fact]
    public async Task Register_DuplicatePhoneInAnotherFormat_ReturnsContactInUse()
    {
        await RegisterAsync("Bob Builder", phone: "+20 100 123 4567");

        var result = await SendAsync(null, new RegisterCommand("X", null, "+201001234567", Password));

        result.ShouldFailWith(AuthErrors.ContactInUse);
    }

    [Fact]
    public async Task Register_MissingNameContactAndShortPassword_ThrowsValidationException()
    {
        var exception = await Should.ThrowAsync<ValidationException>(() =>
            SendAsync(null, new RegisterCommand("", null, null, "short")));

        exception.Errors.Keys.ShouldBe(["DisplayName", "Contact", "Password"], ignoreOrder: true);
    }

    [Fact]
    public async Task Register_InvalidEmailPhoneAndPasswordWithoutDigit_ThrowsValidationException()
    {
        var exception = await Should.ThrowAsync<ValidationException>(() =>
            SendAsync(null, new RegisterCommand("X", "not-an-email", "123", "abcdefgh")));

        exception.Errors.Keys.ShouldBe(["Email", "PhoneNumber", "Password"], ignoreOrder: true);
    }

    [Fact]
    public async Task Login_EmailInDifferentCase_Succeeds()
    {
        await RegisterAsync("Alice", "Alice@Example.com");

        var result = await SendAsync(null, new LoginCommand("ALICE@example.com", Password));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task Login_FormattedPhoneNumber_SignsInThatUser()
    {
        await RegisterAsync("Alice", "alice@example.com");
        var bob = await RegisterAsync("Bob Builder", phone: "+20 100 123 4567");

        var result = await SendAsync(null, new LoginCommand("+20 (100) 123-4567", Password));

        result.ShouldSucceed().User.Id.ShouldBe(bob.User.Id);
    }

    [Fact]
    public async Task Login_UnknownUser_VerifiesPasswordAgainstDummyHashComputedOnce()
    {
        await CreateUserAsync("Alice");

        var result = await SendAsync(null, new LoginCommand("nobody@example.com", Password));

        result.ShouldFailWith(AuthErrors.InvalidCredentials);
        var verifiedHash = Hasher.VerifiedHashes.ShouldHaveSingleItem();
        RecordingPasswordHasher.TimesProduced(verifiedHash).ShouldBe(1);
        var storedHashes = await QueryDbAsync(db => db.UserProfiles.Select(u => u.PasswordHash).ToListAsync());
        storedHashes.ShouldNotContain(verifiedHash);
    }

    [Fact]
    public async Task Login_UnparseableIdentifier_VerifiesAgainstSameDummyHashWithoutRehashing()
    {
        await SendAsync(null, new LoginCommand("nobody@example.com", Password));
        var hashCallsBefore = Hasher.HashCalls;

        var garbage = await SendAsync(null, new LoginCommand("not a contact", Password));
        await SendAsync(null, new LoginCommand("nobody2@example.com", Password));

        garbage.ShouldFailWith(AuthErrors.InvalidCredentials);
        Hasher.HashCalls.ShouldBe(hashCallsBefore);
        Hasher.VerifiedHashes.Count.ShouldBe(3);
        Hasher.VerifiedHashes.Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Login_UnknownUserAndWrongPassword_DoTheSamePasswordHashingWork()
    {
        await CreateUserAsync("Alice");

        await SendAsync(null, new LoginCommand("ghost@example.com", Password));
        await SendAsync(null, new LoginCommand("alice@example.com", "wrong1234"));

        Hasher.VerifiedHashes.Count.ShouldBe(2);
        var dummyHash = Convert.FromBase64String(Hasher.VerifiedHashes[0]);
        var userHash = Convert.FromBase64String(Hasher.VerifiedHashes[1]);
        dummyHash.Length.ShouldBe(userHash.Length);
        dummyHash[..13].ShouldBe(userHash[..13], "format marker, PRF, iteration count and salt size must match");
    }

    [Theory]
    [InlineData("alice@example.com", "wrong1234")]
    [InlineData("nobody@example.com", Password)]
    [InlineData("not a contact", Password)]
    public async Task Login_InvalidCredentials_ReturnsTheSameError(string identifier, string password)
    {
        await CreateUserAsync("Alice");

        var result = await SendAsync(null, new LoginCommand(identifier, password));

        result.ShouldFailWith(AuthErrors.InvalidCredentials);
    }

    [Fact]
    public async Task Refresh_ValidToken_RotatesTheRefreshToken()
    {
        await CreateUserAsync("Alice");
        var login = await LoginAsync("alice@example.com");

        var refreshed = (await RefreshAsync(login.RefreshToken)).ShouldSucceed();

        refreshed.RefreshToken.ShouldNotBe(login.RefreshToken);
    }

    [Fact]
    public async Task Refresh_AlreadyRotatedToken_ReturnsTokenReused()
    {
        await CreateUserAsync("Alice");
        var login = await LoginAsync("alice@example.com");
        (await RefreshAsync(login.RefreshToken)).ShouldSucceed();

        var reuse = await RefreshAsync(login.RefreshToken);

        reuse.ShouldFailWith(AuthErrors.TokenReused);
    }

    [Fact]
    public async Task Refresh_AfterReuseDetected_TokenIssuedByTheRotationIsRevoked()
    {
        await CreateUserAsync("Alice");
        var login = await LoginAsync("alice@example.com");
        var rotated = (await RefreshAsync(login.RefreshToken)).ShouldSucceed();
        (await RefreshAsync(login.RefreshToken)).ShouldFailWith(AuthErrors.TokenReused);

        var afterReuse = await RefreshAsync(rotated.RefreshToken);

        afterReuse.ShouldFailWith(AuthErrors.InvalidRefreshToken);
    }

    [Fact]
    public async Task Refresh_AfterReuseDetected_AllOtherSessionsAreRevoked()
    {
        var alice = await RegisterAsync("Alice", "alice@example.com");
        var login = await LoginAsync("alice@example.com");
        (await RefreshAsync(login.RefreshToken)).ShouldSucceed();
        (await RefreshAsync(login.RefreshToken)).ShouldFailWith(AuthErrors.TokenReused);

        var registerSession = await RefreshAsync(alice.RefreshToken);

        registerSession.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Refresh_UnknownToken_ReturnsInvalidRefreshToken()
    {
        var result = await RefreshAsync("nope");

        result.ShouldFailWith(AuthErrors.InvalidRefreshToken);
    }

    [Fact]
    public async Task Logout_CalledTwiceWithTheSameToken_SucceedsBothTimes()
    {
        var bob = await RegisterAsync("Bob", "bob@example.com");

        var first = await SendAsync(null, new LogoutCommand(bob.RefreshToken));
        var second = await SendAsync(null, new LogoutCommand(bob.RefreshToken));

        first.ShouldSucceed();
        second.ShouldSucceed();
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_ReturnsInvalidCurrentPassword()
    {
        var carol = await CreateUserAsync("Carol");

        var result = await SendAsync(carol, new ChangePasswordCommand("nope12345", "newpass123"));

        result.ShouldFailWith(AuthErrors.InvalidCurrentPassword);
    }

    [Fact]
    public async Task ChangePassword_CorrectCurrentPassword_Succeeds()
    {
        var carol = await CreateUserAsync("Carol");

        var result = await SendAsync(carol, new ChangePasswordCommand(Password, "newpass123"));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task ChangePassword_Success_RevokesExistingSessions()
    {
        var carol = await CreateUserAsync("Carol");
        var otherSession = await LoginAsync("carol@example.com");

        (await SendAsync(carol, new ChangePasswordCommand(Password, "newpass123"))).ShouldSucceed();

        (await RefreshAsync(otherSession.RefreshToken)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ChangePassword_Success_ReturnsAWorkingTokenPair()
    {
        var carol = await CreateUserAsync("Carol");

        var changed = (await SendAsync(carol, new ChangePasswordCommand(Password, "newpass123"))).ShouldSucceed();

        (await RefreshAsync(changed.RefreshToken)).ShouldSucceed();
    }

    [Fact]
    public async Task ChangePassword_Success_LoginRequiresTheNewPassword()
    {
        var carol = await CreateUserAsync("Carol");

        (await SendAsync(carol, new ChangePasswordCommand(Password, "newpass123"))).ShouldSucceed();

        (await SendAsync(null, new LoginCommand("carol@example.com", Password))).IsFailure.ShouldBeTrue();
        (await SendAsync(null, new LoginCommand("carol@example.com", "newpass123"))).ShouldSucceed();
    }
}
