using ChatHub.Domain.Entities;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.UnitTests;

public sealed class PasswordResetTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    [Fact]
    public void RequestPasswordReset_VerifiedEmail_IssuesATokenForThatAddressAndRaisesTheEvent()
    {
        var user = VerifiedUser();

        var reset = user.RequestPasswordReset("raw-token", "token-hash", Now, Lifetime);

        (reset.UserId, reset.Email, reset.TokenHash, reset.CreatedAtUtc, reset.ExpiresAtUtc, reset.IsUsed)
            .ShouldBe((user.Id, "bob@example.com", "token-hash", Now, Now + Lifetime, false));
        var requested = user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PasswordResetRequestedEvent>();
        (requested.UserId, requested.Email, requested.Token, requested.ExpiresAtUtc)
            .ShouldBe((user.Id, "bob@example.com", "raw-token", Now + Lifetime));
    }

    [Fact]
    public void RequestPasswordReset_UnverifiedOrPhoneOnlyOrDeleted_Throws()
    {
        var unverified = UserProfile.Create(TestData.AnyUsername(), "Bob", "hash", email: Email.Create("bob@example.com"));
        var phoneOnly = UserProfile.Create(TestData.AnyUsername(), "Omar", "hash", PhoneNumber.Create("01001234567"));
        var deleted = VerifiedUser();
        deleted.Delete();

        foreach (var user in new[] { unverified, phoneOnly, deleted })
            Should.Throw<DomainRuleViolationException>(() => user.RequestPasswordReset("t", "h", Now, Lifetime));
    }

    [Fact]
    public void ResetPassword_ValidToken_ChangesTheHashRotatesTheStampUsesTheTokenAndRaisesCompleted()
    {
        var user = VerifiedUser();
        var reset = user.RequestPasswordReset("t", "h", Now, Lifetime);
        var stamp = user.SecurityStamp;
        user.ClearDomainEvents();

        user.ResetPassword(reset, "new-hash", Now + TimeSpan.FromMinutes(5));

        user.PasswordHash.ShouldBe("new-hash");
        user.SecurityStamp.ShouldNotBe(stamp);
        reset.UsedAtUtc.ShouldBe(Now + TimeSpan.FromMinutes(5));
        var completed = user.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<PasswordResetCompletedEvent>();
        (completed.UserId, completed.Email, completed.ChangedAtUtc).ShouldBe((user.Id, "bob@example.com", Now + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ResetPassword_UsedOrExpiredOrInvalidatedToken_ThrowsAndKeepsThePassword()
    {
        var user = VerifiedUser();
        var used = user.RequestPasswordReset("t1", "h1", Now, Lifetime);
        user.ResetPassword(used, "first-hash", Now);
        var expired = user.RequestPasswordReset("t2", "h2", Now, Lifetime);
        var invalidated = user.RequestPasswordReset("t3", "h3", Now, Lifetime);
        invalidated.Invalidate(Now + TimeSpan.FromMinutes(1));

        Should.Throw<DomainRuleViolationException>(() => user.ResetPassword(used, "x", Now));
        Should.Throw<DomainRuleViolationException>(() => user.ResetPassword(expired, "x", Now + Lifetime));
        Should.Throw<DomainRuleViolationException>(() => user.ResetPassword(invalidated, "x", Now + TimeSpan.FromMinutes(1)));
        user.PasswordHash.ShouldBe("first-hash");
    }

    [Fact]
    public void ResetPassword_TokenOfAnotherUserOrForAnOldAddress_Throws()
    {
        var bob = VerifiedUser();
        var other = VerifiedUser();
        var othersToken = other.RequestPasswordReset("t", "h", Now, Lifetime);
        var bobsToken = bob.RequestPasswordReset("t2", "h2", Now, Lifetime);
        bob.ChangeEmail(Email.Create("bob.new@example.com"));

        Should.Throw<DomainRuleViolationException>(() => bob.ResetPassword(othersToken, "x", Now));
        Should.Throw<DomainRuleViolationException>(() => bob.ResetPassword(bobsToken, "x", Now));
        bobsToken.IsUsed.ShouldBeFalse();
    }

    [Fact]
    public void Invalidate_UnusedToken_ExpiresItNowAndLeavesAUsedOneAlone()
    {
        var user = VerifiedUser();
        var open = user.RequestPasswordReset("t1", "h1", Now, Lifetime);
        var used = user.RequestPasswordReset("t2", "h2", Now, Lifetime);
        user.ResetPassword(used, "hash2", Now);

        open.Invalidate(Now + TimeSpan.FromMinutes(2));
        used.Invalidate(Now + TimeSpan.FromMinutes(2));

        open.ExpiresAtUtc.ShouldBe(Now + TimeSpan.FromMinutes(2));
        open.IsExpired(Now + TimeSpan.FromMinutes(2)).ShouldBeTrue();
        used.ExpiresAtUtc.ShouldBe(Now + Lifetime);
    }

    private static UserProfile VerifiedUser()
    {
        var user = UserProfile.Create(TestData.AnyUsername("bob"), "Bob", "hash", email: Email.Create("bob@example.com"));
        var verification = user.RequestEmailVerification("v", "vh", Now - TimeSpan.FromHours(1), TimeSpan.FromHours(24));
        user.VerifyEmail(verification, Now - TimeSpan.FromMinutes(30));
        user.ClearDomainEvents();
        return user;
    }
}
