using ChatHub.Domain.Entities;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.UnitTests;

public sealed class EmailVerificationTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    [Fact]
    public void NewUser_IsUnverifiedWithEmailNotificationsOnAndNoUnsubscribeToken()
    {
        var user = UserWithEmail();

        (user.IsEmailVerified, user.EmailVerifiedAtUtc, user.EmailNotificationsEnabled, user.UnsubscribeTokenHash)
            .ShouldBe((false, null, true, null));
    }

    [Fact]
    public void RequestEmailVerification_IssuesATokenForTheCurrentAddressAndRaisesTheEventWithTheRawToken()
    {
        var user = UserWithEmail();

        var token = user.RequestEmailVerification("raw-token", "hash", Now, Lifetime);

        (token.UserId, token.Email, token.TokenHash, token.CreatedAtUtc, token.ExpiresAtUtc, token.UsedAtUtc)
            .ShouldBe((user.Id, "bob@example.com", "hash", Now, Now + Lifetime, null));
        user.DomainEvents.OfType<EmailVerificationRequestedEvent>().ShouldHaveSingleItem()
            .ShouldBe(new EmailVerificationRequestedEvent(user.Id, "bob@example.com", "raw-token", Now + Lifetime));
    }

    [Fact]
    public void RequestEmailVerification_WithoutAnEmail_Throws()
    {
        var user = UserProfile.Create("Bob", "hash", PhoneNumber.Create("01001234567"));

        Should.Throw<DomainRuleViolationException>(() => user.RequestEmailVerification("raw", "hash", Now, Lifetime));
    }

    [Fact]
    public void RequestEmailVerification_WhenAlreadyVerified_Throws()
    {
        var user = VerifiedUser();

        Should.Throw<DomainRuleViolationException>(() => user.RequestEmailVerification("raw2", "hash2", Now, Lifetime));
    }

    [Fact]
    public void VerifyEmail_ValidToken_MarksTheUserVerifiedAndTheTokenUsed()
    {
        var user = UserWithEmail();
        var token = user.RequestEmailVerification("raw", "hash", Now, Lifetime);
        user.ClearDomainEvents();

        user.VerifyEmail(token, Now.AddHours(1));

        (user.IsEmailVerified, user.EmailVerifiedAtUtc).ShouldBe((true, Now.AddHours(1)));
        (token.IsUsed, token.UsedAtUtc).ShouldBe((true, Now.AddHours(1)));
        user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new EmailVerifiedEvent(user.Id));
    }

    [Fact]
    public void VerifyEmail_ExpiredToken_ThrowsAndChangesNothing()
    {
        var user = UserWithEmail();
        var token = user.RequestEmailVerification("raw", "hash", Now, Lifetime);

        Should.Throw<DomainRuleViolationException>(() => user.VerifyEmail(token, Now + Lifetime));

        (user.IsEmailVerified, token.IsUsed).ShouldBe((false, false));
    }

    [Fact]
    public void VerifyEmail_TokenIssuedForAPreviousAddress_Throws()
    {
        var user = UserWithEmail();
        var token = user.RequestEmailVerification("raw", "hash", Now, Lifetime);
        user.ChangeEmail(Email.Create("robert@example.com"));

        Should.Throw<DomainRuleViolationException>(() => user.VerifyEmail(token, Now.AddMinutes(1)));
    }

    [Fact]
    public void VerifyEmail_TokenOfAnotherUser_Throws()
    {
        var bob = UserWithEmail();
        var alice = UserProfile.Create("Alice", "hash", email: Email.Create("bob@example.com"));
        var token = alice.RequestEmailVerification("raw", "hash", Now, Lifetime);

        Should.Throw<DomainRuleViolationException>(() => bob.VerifyEmail(token, Now.AddMinutes(1)));
    }

    [Fact]
    public void ChangeEmail_ResetsVerification()
    {
        var user = VerifiedUser();

        user.ChangeEmail(Email.Create("robert@example.com"));

        (user.IsEmailVerified, user.EmailVerifiedAtUtc).ShouldBe((false, null));
    }

    [Fact]
    public void ChangeEmail_ToTheSameAddress_KeepsVerification()
    {
        var user = VerifiedUser();

        user.ChangeEmail(Email.Create("BOB@example.com"));

        user.IsEmailVerified.ShouldBeTrue();
    }

    [Fact]
    public void Invalidate_UnusedToken_ExpiresItNowButLeavesAUsedOneAlone()
    {
        var user = UserWithEmail();
        var unused = user.RequestEmailVerification("raw", "hash", Now, Lifetime);

        unused.Invalidate(Now.AddMinutes(5));

        unused.ExpiresAtUtc.ShouldBe(Now.AddMinutes(5));
        unused.IsExpired(Now.AddMinutes(5)).ShouldBeTrue();

        var used = user.RequestEmailVerification("raw2", "hash2", Now, Lifetime);
        user.VerifyEmail(used, Now.AddMinutes(1));
        used.Invalidate(Now.AddMinutes(5));
        used.ExpiresAtUtc.ShouldBe(Now + Lifetime);
    }

    [Fact]
    public void SetEmailNotifications_RaisesAnEventOnlyWhenTheValueChanges()
    {
        var user = UserWithEmail();

        user.SetEmailNotifications(true);
        user.SetEmailNotifications(false);
        user.SetEmailNotifications(false);

        user.EmailNotificationsEnabled.ShouldBeFalse();
        user.DomainEvents.ShouldHaveSingleItem().ShouldBe(new EmailNotificationsChangedEvent(user.Id, false));
    }

    [Fact]
    public void IssueUnsubscribeToken_StoresTheHashAndRotatesOnTheNextIssue()
    {
        var user = UserWithEmail();

        user.IssueUnsubscribeToken("first", "hash-1");
        user.IssueUnsubscribeToken("second", "hash-2");

        user.UnsubscribeTokenHash.ShouldBe("hash-2");
        user.DomainEvents.OfType<UnsubscribeTokenIssuedEvent>().Select(e => e.Token).ShouldBe(["first", "second"]);
    }

    [Fact]
    public void DeletedUser_CannotRequestVerificationOrChangePreferences()
    {
        var user = UserWithEmail();
        user.Delete();

        Should.Throw<DomainRuleViolationException>(() => user.RequestEmailVerification("raw", "hash", Now, Lifetime));
        Should.Throw<DomainRuleViolationException>(() => user.SetEmailNotifications(false));
        Should.Throw<DomainRuleViolationException>(() => user.IssueUnsubscribeToken("raw", "hash"));
    }

    private static UserProfile UserWithEmail() => UserProfile.Create("Bob", "hash", email: Email.Create("bob@example.com"));

    private static UserProfile VerifiedUser()
    {
        var user = UserWithEmail();
        user.VerifyEmail(user.RequestEmailVerification("raw", "hash", Now, Lifetime), Now.AddMinutes(1));
        return user;
    }
}
