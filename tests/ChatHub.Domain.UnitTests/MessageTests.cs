using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Events;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Domain.UnitTests;

public sealed class MessageTests
{
    private readonly Guid _conversationId = Guid.NewGuid();
    private readonly Guid _senderId = Guid.NewGuid();

    [Fact]
    public void Create_Text_TrimsTheContentAndRaisesMessageSent()
    {
        var message = Message.Create(_conversationId, _senderId, MessageType.Text, "  hello  ");

        message.Content.ShouldBe("hello");
        message.IsEdited.ShouldBeFalse();
        message.IsDeleted.ShouldBeFalse();
        message.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new MessageSentEvent(message.Id, _conversationId, _senderId, message.CreatedAtUtc));
    }

    [Fact]
    public void Create_AnyMessage_RecordsTheSenderAsCreator()
    {
        var message = Message.Create(_conversationId, _senderId, MessageType.Text, "hello");

        message.CreatedById.ShouldBe(_senderId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Create_EmptyId_Throws(bool emptyConversation, bool emptySender)
    {
        Should.Throw<DomainRuleViolationException>(() => Message.Create(
            emptyConversation ? Guid.Empty : _conversationId, emptySender ? Guid.Empty : _senderId, MessageType.Text, "x"));
    }

    [Fact]
    public void Create_UndefinedType_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() => Message.Create(_conversationId, _senderId, (MessageType)42, "x"));
    }

    [Fact]
    public void Create_EmptyParentMessageId_Throws()
    {
        Should.Throw<DomainRuleViolationException>(() =>
            Message.Create(_conversationId, _senderId, MessageType.Text, "x", parentMessageId: Guid.Empty));
    }

    [Theory]
    [InlineData(MessageType.Text, null)]
    [InlineData(MessageType.Text, "   ")]
    [InlineData(MessageType.System, null)]
    public void Create_TextOrSystemWithoutContent_Throws(MessageType type, string? content)
    {
        Should.Throw<DomainRuleViolationException>(() => Message.Create(_conversationId, _senderId, type, content));
    }

    [Theory]
    [InlineData(MessageType.Text)]
    [InlineData(MessageType.System)]
    public void Create_TextOrSystemWithAnAttachment_Throws(MessageType type)
    {
        Should.Throw<DomainRuleViolationException>(() =>
            Message.Create(_conversationId, _senderId, type, "x", TestData.Png()));
    }

    [Theory]
    [InlineData(MessageType.Image)]
    [InlineData(MessageType.File)]
    [InlineData(MessageType.Voice)]
    public void Create_AttachmentTypeWithoutAnAttachment_Throws(MessageType type)
    {
        Should.Throw<DomainRuleViolationException>(() => Message.Create(_conversationId, _senderId, type, "caption"));
    }

    [Fact]
    public void Create_ContentLongerThanTheMaximum_Throws()
    {
        var content = new string('x', Message.MaxContentLength + 1);

        Should.Throw<DomainRuleViolationException>(() => Message.Create(_conversationId, _senderId, MessageType.Text, content));
    }

    [Fact]
    public void Create_ImageWithAttachmentAndCaption_KeepsBoth()
    {
        var attachment = TestData.Png();

        var message = Message.Create(_conversationId, _senderId, MessageType.Image, "look", attachment);

        message.Attachment.ShouldBe(attachment);
        message.Content.ShouldBe("look");
    }

    [Fact]
    public void EditContent_BySender_UpdatesTheContentAndRaisesMessageEdited()
    {
        var message = Created("hello");

        message.EditContent(_senderId, "  hello!  ");

        message.Content.ShouldBe("hello!");
        message.IsEdited.ShouldBeTrue();
        message.EditedAtUtc.ShouldNotBeNull();
        message.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MessageEditedEvent(message.Id, _conversationId));
    }

    [Fact]
    public void EditContent_SameContent_RaisesNoEventAndIsNotMarkedEdited()
    {
        var message = Created("hello");

        message.EditContent(_senderId, " hello ");

        message.IsEdited.ShouldBeFalse();
        message.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void EditContent_ByAnotherUser_Throws()
    {
        var message = Created("hello");

        Should.Throw<DomainRuleViolationException>(() => message.EditContent(Guid.NewGuid(), "hacked"));
    }

    [Fact]
    public void EditContent_NonTextMessage_Throws()
    {
        var message = Message.Create(_conversationId, _senderId, MessageType.Image, null, TestData.Png());

        Should.Throw<DomainRuleViolationException>(() => message.EditContent(_senderId, "caption"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EditContent_BlankContent_Throws(string content)
    {
        var message = Created("hello");

        Should.Throw<DomainRuleViolationException>(() => message.EditContent(_senderId, content));
    }

    [Fact]
    public void EditContent_LongerThanTheMaximum_Throws()
    {
        var message = Created("hello");

        Should.Throw<DomainRuleViolationException>(() => message.EditContent(_senderId, new string('x', Message.MaxContentLength + 1)));
    }

    [Fact]
    public void EditContent_DeletedMessage_Throws()
    {
        var message = Created("hello");
        message.Delete(_senderId);

        Should.Throw<DomainRuleViolationException>(() => message.EditContent(_senderId, "x"));
    }

    [Fact]
    public void Delete_BySender_ClearsContentAndAttachmentAndRaisesMessageDeletedWithTheFileId()
    {
        var fileId = Guid.NewGuid();
        var message = Message.Create(_conversationId, _senderId, MessageType.Image, "look", TestData.Png(fileId));
        message.ClearDomainEvents();

        message.Delete(_senderId);

        message.IsDeleted.ShouldBeTrue();
        message.Content.ShouldBeNull();
        message.Attachment.ShouldBeNull();
        message.DomainEvents.ShouldHaveSingleItem().ShouldBe(new MessageDeletedEvent(message.Id, _conversationId, fileId));
    }

    [Fact]
    public void Delete_ByAnotherUser_Throws()
    {
        var message = Created("hello");

        Should.Throw<DomainRuleViolationException>(() => message.Delete(Guid.NewGuid()));
    }

    [Fact]
    public void Delete_AlreadyDeleted_IsIdempotent()
    {
        var message = Created("hello");
        message.Delete(_senderId);
        message.ClearDomainEvents();

        message.Delete(_senderId);

        message.DomainEvents.ShouldBeEmpty();
    }

    private Message Created(string content)
    {
        var message = Message.Create(_conversationId, _senderId, MessageType.Text, content);
        message.ClearDomainEvents();
        return message;
    }
}
