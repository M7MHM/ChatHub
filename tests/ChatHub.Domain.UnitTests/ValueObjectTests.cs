using ChatHub.Domain.Entities;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;
using ChatHub.Domain.ValueObjects;

namespace ChatHub.Domain.UnitTests;

public sealed class EmailTests
{
    [Fact]
    public void Create_MixedCaseWithSurroundingSpaces_IsTrimmedAndLowercased()
    {
        var email = Email.Create("  Alice@Example.COM ");

        email.Value.ShouldBe("alice@example.com");
        email.ToString().ShouldBe("alice@example.com");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Blank_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => Email.Create(value));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("alice@localhost")]
    [InlineData("alice@@example.com")]
    [InlineData("Alice <alice@example.com>")]
    public void Create_InvalidFormat_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => Email.Create(value));
    }

    [Fact]
    public void Create_LongerThan254Characters_Throws()
    {
        var value = new string('a', 64) + "@" + string.Join('.', Enumerable.Repeat(new string('b', 60), 4)) + ".com";

        Should.Throw<DomainRuleViolationException>(() => Email.Create(value));
    }

    [Fact]
    public void Equality_SameAddressInDifferentCase_IsEqual()
    {
        var a = Email.Create("alice@example.com");
        var b = Email.Create("ALICE@example.com");

        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        (a != Email.Create("bob@example.com")).ShouldBeTrue();
    }
}

public sealed class PhoneNumberTests
{
    [Fact]
    public void Create_FormattedNumber_IgnoresSpacesDashesAndParentheses()
    {
        var phone = PhoneNumber.Create("+20 (100) 123-4567");

        phone.FullNumber.ShouldBe("+201001234567");
        phone.ToString().ShouldBe("+201001234567");
    }

    [Theory]
    [InlineData("+12025550123", "1", "2025550123")]
    [InlineData("+79161234567", "7", "9161234567")]
    [InlineData("+201001234567", "20", "1001234567")]
    [InlineData("+971501234567", "971", "501234567")]
    public void Create_ValidNumber_SplitsTheCountryCode(string value, string countryCode, string nationalNumber)
    {
        var phone = PhoneNumber.Create(value);

        phone.CountryCode.ShouldBe(countryCode);
        phone.NationalNumber.ShouldBe(nationalNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Blank_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create(value));
    }

    [Fact]
    public void Create_WithoutLeadingPlus_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create("201001234567"));
    }

    [Theory]
    [InlineData("+1234567")]
    [InlineData("+1234567890123456")]
    [InlineData("+0123456789")]
    [InlineData("+20abc123456")]
    public void Create_NotEightToFifteenE164Digits_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create(value));
    }

    [Fact]
    public void Equality_SameNumberDifferentlyFormatted_IsEqual()
    {
        (PhoneNumber.Create("+20 100 123 4567") == PhoneNumber.Create("+201001234567")).ShouldBeTrue();
    }
}

public sealed class AttachmentTests
{
    [Fact]
    public void Create_ValidValues_KeepsThem()
    {
        var fileId = Guid.NewGuid();

        var attachment = Attachment.Create(fileId, "a.png", "image/png", 10);

        attachment.FileId.ShouldBe(fileId);
        attachment.FileName.ShouldBe("a.png");
        attachment.ContentType.ShouldBe("image/png");
        attachment.FileSizeBytes.ShouldBe(10);
    }

    [Theory]
    [InlineData(true, "a.png", "image/png", 10)]
    [InlineData(false, " ", "image/png", 10)]
    [InlineData(false, "a.png", " ", 10)]
    [InlineData(false, "a.png", "image/png", 0)]
    public void Create_InvalidValue_Throws(bool emptyFileId, string fileName, string contentType, long size)
    {
        Should.Throw<DomainRuleViolationException>(() =>
            Attachment.Create(emptyFileId ? Guid.Empty : Guid.NewGuid(), fileName, contentType, size));
    }

    [Fact]
    public void Equality_SameValues_IsEqualAndNullIsNot()
    {
        var fileId = Guid.NewGuid();
        var a = Attachment.Create(fileId, "a.png", "image/png", 10);

        (a == Attachment.Create(fileId, "a.png", "image/png", 10)).ShouldBeTrue();
        (a == null).ShouldBeFalse();
        a.Equals(Email.Create("a@example.com")).ShouldBeFalse();
    }
}

public sealed class BaseEntityTests
{
    [Fact]
    public void NewEntity_Id_IsAVersion7Guid()
    {
        var block = UserBlock.Create(Guid.NewGuid(), Guid.NewGuid());

        block.Id.Version.ShouldBe(7);
    }

    [Fact]
    public void DomainEvents_AddRemoveAndClear_TrackTheRaisedEvents()
    {
        var block = UserBlock.Create(Guid.NewGuid(), Guid.NewGuid());
        var first = new UserProfileUpdatedEvent(Guid.NewGuid());
        var second = new UserProfileUpdatedEvent(Guid.NewGuid());

        block.AddDomainEvent(first);
        block.AddDomainEvent(second);
        block.RemoveDomainEvent(first);
        var afterRemove = block.DomainEvents.ToList();
        block.ClearDomainEvents();

        afterRemove.ShouldBe([second]);
        block.DomainEvents.ShouldBeEmpty();
    }
}
