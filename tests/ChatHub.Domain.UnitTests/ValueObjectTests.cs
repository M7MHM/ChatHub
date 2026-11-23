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
    [Theory]
    [InlineData("01001234567")]
    [InlineData("1001234567")]
    [InlineData("+201001234567")]
    [InlineData("+20 100 123 4567")]
    [InlineData("+20 (100) 123-4567")]
    [InlineData("0100-123-4567")]
    [InlineData("00201001234567")]
    public void Create_EgyptianMobileInAnyFormat_NormalizesToE164(string value)
    {
        var phone = PhoneNumber.Create(value);

        phone.FullNumber.ShouldBe("+201001234567");
        phone.ToString().ShouldBe("+201001234567");
        (phone.CountryCode, phone.NationalNumber, phone.IsValid).ShouldBe(("20", "1001234567", true));
    }

    [Theory]
    [InlineData("01112223333", "+201112223333")]
    [InlineData("01212345678", "+201212345678")]
    [InlineData("01512345678", "+201512345678")]
    public void Create_EachEgyptianMobileOperator_IsAccepted(string value, string expected)
    {
        PhoneNumber.Create(value).FullNumber.ShouldBe(expected);
    }

    [Theory]
    [InlineData("+12015550123", "1", "2015550123")]
    [InlineData("+447911123456", "44", "7911123456")]
    [InlineData("+79161234567", "7", "9161234567")]
    [InlineData("+971501234567", "971", "501234567")]
    public void Create_OtherCountryInInternationalForm_IsAccepted(string value, string countryCode, string nationalNumber)
    {
        var phone = PhoneNumber.Create(value);

        phone.FullNumber.ShouldBe(value);
        (phone.CountryCode, phone.NationalNumber).ShouldBe((countryCode, nationalNumber));
    }

    [Theory]
    [InlineData("0100123456")]
    [InlineData("010012345678")]
    [InlineData("100123456")]
    [InlineData("+20100123456")]
    [InlineData("+2010012345678")]
    [InlineData("01301234567")]
    public void Create_InvalidLengthOrPrefix_ThrowsWithTheMobileMessage(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create(value))
            .Message.ShouldBe(PhoneNumber.InvalidMessage);
    }

    [Theory]
    [InlineData("0223456789")]
    [InlineData("+20223456789")]
    [InlineData("034567890")]
    public void Create_EgyptianLandline_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create(value))
            .Message.ShouldBe(PhoneNumber.InvalidMessage);
    }

    [Theory]
    [InlineData("447911123456")]
    [InlineData("971501234567")]
    public void Create_ForeignNumberWithoutPlus_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create(value));
    }

    [Theory]
    [InlineData("+20abc123456")]
    [InlineData("0100abc4567")]
    [InlineData("not a phone")]
    public void Create_LettersOrGarbage_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_Blank_Throws(string value)
    {
        Should.Throw<DomainRuleViolationException>(() => PhoneNumber.Create(value));
    }

    [Fact]
    public void Equality_SameNumberInLocalAndInternationalFormat_IsEqual()
    {
        var local = PhoneNumber.Create("01001234567");
        var international = PhoneNumber.Create("+20 100 123 4567");

        (local == international).ShouldBeTrue();
        local.GetHashCode().ShouldBe(international.GetHashCode());
        (local != PhoneNumber.Create("01001234568")).ShouldBeTrue();
    }

    [Fact]
    public void FromStorage_ValidE164Mobile_LoadsAsValid()
    {
        var phone = PhoneNumber.FromStorage("+201001234567");

        phone.ShouldBe(PhoneNumber.Create("01001234567"));
        phone.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("+20223456789")]
    [InlineData("+2010012345")]
    [InlineData("+99912345678")]
    public void FromStorage_ValueTheOldRulesAllowed_LoadsUnchangedAndIsFlaggedInvalid(string stored)
    {
        var phone = PhoneNumber.FromStorage(stored);

        phone.FullNumber.ShouldBe(stored);
        phone.IsValid.ShouldBeFalse();
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
