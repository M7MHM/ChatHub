using ChatHub.Domain.Entities;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class RefreshTokenTests
{
    private static readonly DateTime NextWeek = DateTime.UtcNow.AddDays(7);

    [Fact]
    public void Create_FutureExpiry_CreatesAnActiveToken()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "hash", NextWeek);

        token.IsActive(DateTime.UtcNow).ShouldBeTrue();
        token.RevokedAtUtc.ShouldBeNull();
        token.ReplacedByTokenHash.ShouldBeNull();
    }

    [Fact]
    public void Create_EmptyUserId_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => RefreshToken.Create(Guid.Empty, "hash", NextWeek));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankTokenHash_Throws(string hash)
    {
        Should.Throw<DomainRuleViolationException>(() => RefreshToken.Create(Guid.NewGuid(), hash, NextWeek));
    }

    [Fact]
    public void Create_ExpiryNotInTheFuture_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => RefreshToken.Create(Guid.NewGuid(), "hash", DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public void Revoke_WithAReplacement_RecordsTheReplacementHashAndDeactivates()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "hash", NextWeek);

        token.Revoke("next-hash");

        token.RevokedAtUtc.ShouldNotBeNull();
        token.ReplacedByTokenHash.ShouldBe("next-hash");
        token.IsActive(DateTime.UtcNow).ShouldBeFalse();
    }

    [Fact]
    public void Revoke_BlankReplacement_StoresNoReplacement()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "hash", NextWeek);

        token.Revoke("  ");

        token.ReplacedByTokenHash.ShouldBeNull();
    }

    [Fact]
    public void Revoke_AlreadyRevoked_KeepsTheFirstRevocation()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "hash", NextWeek);
        token.Revoke();
        var firstRevokedAt = token.RevokedAtUtc;

        token.Revoke("next-hash");

        token.RevokedAtUtc.ShouldBe(firstRevokedAt);
        token.ReplacedByTokenHash.ShouldBeNull();
    }

    [Fact]
    public void IsActive_AtOrAfterExpiry_IsFalse()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "hash", NextWeek);

        token.IsExpired(NextWeek).ShouldBeTrue();
        token.IsActive(NextWeek).ShouldBeFalse();
        token.IsActive(NextWeek.AddTicks(-1)).ShouldBeTrue();
    }
}

public sealed class UserBlockTests
{
    [Fact]
    public void Create_TwoDifferentUsers_RecordsTheBlock()
    {
        var blocker = Guid.NewGuid();
        var blocked = Guid.NewGuid();

        var block = UserBlock.Create(blocker, blocked);

        block.BlockerId.ShouldBe(blocker);
        block.BlockedId.ShouldBe(blocked);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Create_EmptyUserId_Throws(bool emptyBlocker, bool emptyBlocked)
    {
        Should.Throw<DomainRuleViolationException>(() => UserBlock.Create(
            emptyBlocker ? Guid.Empty : Guid.NewGuid(), emptyBlocked ? Guid.Empty : Guid.NewGuid()));
    }

    [Fact]
    public void Create_Self_Throws()
    {
        var user = Guid.NewGuid();

        Should.Throw<DomainRuleViolationException>(() => UserBlock.Create(user, user));
    }
}
