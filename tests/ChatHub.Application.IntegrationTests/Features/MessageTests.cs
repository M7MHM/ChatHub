using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Messages;
using ChatHub.Application.Features.Messages.Commands;
using ChatHub.Application.Features.Messages.Queries;
using ChatHub.Application.Features.Users;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Enums;
using ChatHub.Domain.Exceptions;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class MessageTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task SendMessage_Text_ReturnsTheTrimmedProjection()
    {
        var crew = await CreateCrewAsync();

        var message = await SendTextAsync(crew.Paula, crew.Group, "  hello crew  ");

        message.Type.ShouldBe(MessageType.Text);
        message.Content.ShouldBe("hello crew");
        message.Attachment.ShouldBeNull();
        message.ReplyTo.ShouldBeNull();
        message.IsDeleted.ShouldBeFalse();
        message.IsEdited.ShouldBeFalse();
        message.Sender.Id.ShouldBe(crew.Paula);
        message.Sender.DisplayName.ShouldBe("Paula");
        message.ConversationId.ShouldBe(crew.Group);
    }

    [Fact]
    public async Task SendMessage_ImageWithCaption_AttachesTheUploadedFileWithASignedUrl()
    {
        var crew = await CreateCrewAsync();
        var file = await UploadPngAsync(crew.Quinn, fileName: "a.png");

        var message = await SendFileAsync(crew.Quinn, crew.Group, MessageType.Image, file.Id, "look");

        var attachment = message.Attachment.ShouldNotBeNull();
        attachment.FileName.ShouldBe("a.png");
        attachment.ContentType.ShouldBe("image/png");
        attachment.FileId.ShouldBe(file.Id);
        attachment.SizeBytes.ShouldBe(TestFiles.Png.Length);
        attachment.Url.ShouldStartWith($"/api/files/{file.Id}?exp=");
        message.Content.ShouldBe("look");
    }

    [Fact]
    public async Task SendMessage_Voice_AttachesTheAudioFile()
    {
        var crew = await CreateCrewAsync();
        var file = await UploadAsync(crew.Rex, FilePurpose.MessageAttachment, TestFiles.Ogg, "v.ogg", "audio/ogg");

        var message = await SendFileAsync(crew.Rex, crew.Group, MessageType.Voice, file.Id);

        message.Type.ShouldBe(MessageType.Voice);
        message.Attachment.ShouldNotBeNull().ContentType.ShouldBe("audio/ogg");
        message.Content.ShouldBeNull();
    }

    [Fact]
    public async Task SendMessage_File_AcceptsAnyContentType()
    {
        var crew = await CreateCrewAsync();
        var file = await UploadAsync(crew.Paula, FilePurpose.MessageAttachment, TestFiles.Pdf, "r.pdf", "application/pdf");

        var message = await SendFileAsync(crew.Paula, crew.Group, MessageType.File, file.Id);

        message.Type.ShouldBe(MessageType.File);
        message.Attachment.ShouldNotBeNull().FileName.ShouldBe("r.pdf");
    }

    [Fact]
    public async Task SendMessage_SystemType_ThrowsValidationException()
    {
        var crew = await CreateCrewAsync();

        var exception = await Should.ThrowAsync<ValidationException>(() =>
            TrySendAsync(crew.Paula, crew.Group, MessageType.System, "x"));

        exception.Errors.ShouldContainKey("Type");
    }

    [Theory]
    [InlineData(MessageType.Image, "audio/ogg")]
    [InlineData(MessageType.Voice, "image/png")]
    public async Task SendMessage_FileTypeNotMatchingTheMessageType_ReturnsTypeMismatch(MessageType type, string contentType)
    {
        var crew = await CreateCrewAsync();
        var file = contentType == "audio/ogg"
            ? await UploadAsync(crew.Paula, FilePurpose.MessageAttachment, TestFiles.Ogg, "a.ogg", contentType)
            : await UploadPngAsync(crew.Paula);

        var result = await TrySendAsync(crew.Paula, crew.Group, type, null, file.Id);

        result.ShouldFailWith(FileErrors.TypeMismatch);
    }

    [Fact]
    public async Task SendMessage_FileNotOwnedOrAlreadyUsed_IsRejected()
    {
        var crew = await CreateCrewAsync();
        var quinnsFile = await UploadPngAsync(crew.Quinn);
        var pdf = await UploadAsync(crew.Paula, FilePurpose.MessageAttachment, TestFiles.Pdf, "r.pdf", "application/pdf");
        await SendFileAsync(crew.Paula, crew.Group, MessageType.File, pdf.Id);

        var notOwned = await TrySendAsync(crew.Paula, crew.Group, MessageType.Image, null, quinnsFile.Id);
        var reused = await TrySendAsync(crew.Paula, crew.Group, MessageType.File, null, pdf.Id);

        notOwned.ShouldFailWith(FileErrors.NotOwned);
        reused.ShouldFailWith(FileErrors.AlreadyAttached);
    }

    [Theory]
    [InlineData(MessageType.Text, "  ", false, "Content")]
    [InlineData(MessageType.Text, "x", true, "AttachmentFileId")]
    [InlineData(MessageType.Image, null, false, "AttachmentFileId")]
    public async Task SendMessage_ContentAndAttachmentNotMatchingTheType_ThrowsValidationException(
        MessageType type, string? content, bool withAttachment, string invalidProperty)
    {
        var crew = await CreateCrewAsync();

        var exception = await Should.ThrowAsync<ValidationException>(() =>
            TrySendAsync(crew.Paula, crew.Group, type, content, withAttachment ? Guid.NewGuid() : null));

        exception.Errors.ShouldContainKey(invalidProperty);
    }

    [Fact]
    public async Task SendMessage_DirectChatWithABlock_ReturnsBlocked()
    {
        var crew = await CreateCrewAsync();
        var direct = await CreateDirectAsync(crew.Paula, crew.Sam);
        await BlockAsync(crew.Sam, crew.Paula);

        var result = await TrySendAsync(crew.Paula, direct, MessageType.Text, "hi");

        result.ShouldFailWith(ConversationErrors.Blocked);
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public async Task SendMessage_ReplyToAMessageFromAnotherConversation_ReturnsReplyTargetNotFound()
    {
        var crew = await CreateCrewAsync();
        var privateMessage = await SendTextAsync(crew.Paula, crew.Direct, "private");

        var result = await TrySendAsync(crew.Quinn, crew.Group, MessageType.Text, "x", replyToId: privateMessage.Id);

        result.ShouldFailWith(MessageErrors.ReplyTargetNotFound);
        result.Error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task SendMessage_NonMember_ReturnsNotMember()
    {
        var crew = await CreateCrewAsync();

        var result = await TrySendAsync(crew.Sam, crew.Group, MessageType.Text, "x");

        result.ShouldFailWith(ConversationErrors.NotMember);
    }

    [Fact]
    public async Task SendMessage_ReplyToAMessageTheCallerCleared_ReturnsReplyTargetNotFound()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");
        await PauseAsync();
        await ClearChatAsync(crew.Rex, crew.Group);
        await PauseAsync();

        var result = await TrySendAsync(crew.Rex, crew.Group, MessageType.Text, "?", replyToId: hello.Id);

        result.ShouldFailWith(MessageErrors.ReplyTargetNotFound);
    }

    [Fact]
    public async Task SendMessage_Reply_CarriesAPreviewOfTheParent()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");

        var reply = await SendTextAsync(crew.Quinn, crew.Group, "hi paula", hello.Id);

        reply.ReplyTo.ShouldBe(new ReplyPreview(hello.Id, "Paula", "hello crew", MessageType.Text, false));
    }

    [Fact]
    public async Task SendMessage_ReplyToAMessageFromBeforeTheCallerJoined_ReturnsReplyTargetNotFound()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");
        await PauseAsync();
        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);

        var result = await TrySendAsync(crew.Tess, crew.Group, MessageType.Text, "?", replyToId: hello.Id);

        result.ShouldFailWith(MessageErrors.ReplyTargetNotFound);
    }

    [Fact]
    public async Task GetMessages_AnyPage_RunsOneSqlCommand()
    {
        var history = await CreateCrewHistoryAsync();
        Logs.ResetSqlCommands();

        (await SendAsync(history.Crew.Paula, new GetMessagesQuery(null, 3) { ConversationId = history.Crew.Group })).ShouldSucceed();

        Logs.SqlCommands.ShouldBe(1);
    }

    [Fact]
    public async Task GetMessages_SecondPageViaCursor_ContinuesNewestFirstWithoutOverlap()
    {
        var h = await CreateCrewHistoryAsync();

        var first = (await SendAsync(h.Crew.Paula, new GetMessagesQuery(null, 3) { ConversationId = h.Crew.Group })).ShouldSucceed();
        var second = (await SendAsync(h.Crew.Paula, new GetMessagesQuery(first.NextCursor, 3) { ConversationId = h.Crew.Group }))
            .ShouldSucceed();

        first.Items.Select(m => m.Id).ShouldBe([h.TessHi, h.LateReply, h.Reply]);
        first.HasMore.ShouldBeTrue();
        second.Items.Select(m => m.Id).ShouldBe([h.Pdf, h.Voice, h.Image]);
        second.HasMore.ShouldBeTrue();
    }

    [Fact]
    public async Task GetMessages_AllPages_ReturnEveryMessageNewestFirstAndEndWithoutACursor()
    {
        var h = await CreateCrewHistoryAsync();

        var all = await AllMessagesAsync(h.Crew.Paula, h.Crew.Group);

        all.Select(m => m.Id).ShouldBe([h.TessHi, h.LateReply, h.Reply, h.Pdf, h.Voice, h.Image, h.Hello]);
    }

    [Fact]
    public async Task GetMessages_AfterClear_HidesEarlierMessagesAndTheirTextInReplies()
    {
        var h = await CreateCrewHistoryAsync();

        var all = await AllMessagesAsync(h.Crew.Rex, h.Crew.Group);

        all.Select(m => m.Id).ShouldBe([h.TessHi, h.LateReply, h.Reply]);
        var replyTo = all[2].ReplyTo.ShouldNotBeNull();
        replyTo.Text.ShouldBeNull();
        replyTo.SenderName.ShouldBe("Paula");
    }

    [Fact]
    public async Task GetMessages_AfterJoining_HidesEarlierMessages()
    {
        var h = await CreateCrewHistoryAsync();

        var all = await AllMessagesAsync(h.Crew.Tess, h.Crew.Group);

        all.Select(m => m.Id).ShouldBe([h.TessHi, h.LateReply]);
    }

    [Fact]
    public async Task GetMessages_ReplyToAParentTheViewerSaw_ShowsTheParentText()
    {
        var h = await CreateCrewHistoryAsync();

        var all = await AllMessagesAsync(h.Crew.Paula, h.Crew.Group);

        all.Single(m => m.Id == h.Reply).ReplyTo.ShouldNotBeNull().Text.ShouldBe("hello crew");
    }

    [Fact]
    public async Task GetMessages_EmptyConversation_ReturnsAnEmptyLastPage()
    {
        var crew = await CreateCrewAsync();
        var direct = await CreateDirectAsync(crew.Uma, crew.Quinn);

        var page = (await SendAsync(crew.Uma, new GetMessagesQuery { ConversationId = direct })).ShouldSucceed();

        page.Items.ShouldBeEmpty();
        page.HasMore.ShouldBeFalse();
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task GetMessages_NonMemberOrUnknownConversation_ReturnsNotMemberOrNotFound()
    {
        var crew = await CreateCrewAsync();

        var notMember = await SendAsync(crew.Sam, new GetMessagesQuery { ConversationId = crew.Group });
        var unknown = await SendAsync(crew.Sam, new GetMessagesQuery { ConversationId = Guid.NewGuid() });

        notMember.ShouldFailWith(ConversationErrors.NotMember);
        unknown.ShouldFailWith(ConversationErrors.NotFound);
    }

    [Fact]
    public async Task GetMessages_DeletedMessages_AreMasked()
    {
        var h = await CreateCrewHistoryAsync();
        (await SendAsync(h.Crew.Paula, new DeleteMessageCommand(h.Pdf))).ShouldSucceed();
        (await SendAsync(h.Crew.Quinn, new DeleteMessageCommand(h.Image))).ShouldSucceed();

        var all = await AllMessagesAsync(h.Crew.Paula, h.Crew.Group);

        var pdf = all.Single(m => m.Id == h.Pdf);
        pdf.IsDeleted.ShouldBeTrue();
        pdf.Content.ShouldBeNull();
        pdf.Attachment.ShouldBeNull();
        pdf.Type.ShouldBe(MessageType.File);
        var image = all.Single(m => m.Id == h.Image);
        image.IsDeleted.ShouldBeTrue();
        image.Content.ShouldBeNull();
        image.Attachment.ShouldBeNull();
    }

    [Fact]
    public async Task GetMessages_SenderDeletedTheirAccount_MasksTheSenderAndReplyPreviews()
    {
        var crew = await CreateCrewAsync();
        var voiceFile = await UploadAsync(crew.Rex, FilePurpose.MessageAttachment, TestFiles.Ogg, "v.ogg", "audio/ogg");
        var voice = await SendFileAsync(crew.Rex, crew.Group, MessageType.Voice, voiceFile.Id);
        var reply = await SendTextAsync(crew.Paula, crew.Group, "bye rex", voice.Id);

        (await SendAsync(crew.Rex, new DeleteMyAccountCommand())).ShouldSucceed();

        var all = await AllMessagesAsync(crew.Paula, crew.Group);
        all.Single(m => m.Id == voice.Id).Sender.ShouldBe(new UserSummary(crew.Rex, UserSummary.DeletedDisplayName, null, false, null));
        var replyTo = all.Single(m => m.Id == reply.Id).ReplyTo.ShouldNotBeNull();
        replyTo.SenderName.ShouldBe(UserSummary.DeletedDisplayName);
        replyTo.Text.ShouldBe("[Voice]");
    }

    [Fact]
    public async Task EditMessage_ByNonSender_ThrowsDomainRuleViolation()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");

        await Should.ThrowAsync<DomainRuleViolationException>(() =>
            SendAsync(crew.Quinn, new EditMessageCommand("hacked") { MessageId = hello.Id }));
    }

    [Fact]
    public async Task DeleteMessage_ByNonSender_ThrowsDomainRuleViolation()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");

        await Should.ThrowAsync<DomainRuleViolationException>(() => SendAsync(crew.Quinn, new DeleteMessageCommand(hello.Id)));
    }

    [Fact]
    public async Task EditMessage_InAConversationTheCallerIsNotIn_ReturnsNotMember()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");

        var result = await SendAsync(crew.Sam, new EditMessageCommand("x") { MessageId = hello.Id });

        result.ShouldFailWith(ConversationErrors.NotMember);
    }

    [Fact]
    public async Task EditMessage_UnknownMessage_ReturnsNotFound()
    {
        var crew = await CreateCrewAsync();

        var result = await SendAsync(crew.Paula, new EditMessageCommand("x") { MessageId = Guid.NewGuid() });

        result.ShouldFailWith(MessageErrors.NotFound);
    }

    [Fact]
    public async Task EditMessage_BySender_ReturnsTheUpdatedMessage()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");

        var edited = (await SendAsync(crew.Paula, new EditMessageCommand("hello crew!") { MessageId = hello.Id })).ShouldSucceed();

        edited.Content.ShouldBe("hello crew!");
        edited.IsEdited.ShouldBeTrue();
        edited.EditedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task EditMessage_DeletedMessage_ThrowsDomainRuleViolation()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "hello crew");
        (await SendAsync(crew.Paula, new DeleteMessageCommand(hello.Id))).ShouldSucceed();

        await Should.ThrowAsync<DomainRuleViolationException>(() =>
            SendAsync(crew.Paula, new EditMessageCommand("x") { MessageId = hello.Id }));
    }

    [Fact]
    public async Task DeleteMessage_BySender_Succeeds()
    {
        var crew = await CreateCrewAsync();
        var file = await UploadAsync(crew.Paula, FilePurpose.MessageAttachment, TestFiles.Pdf, "r.pdf", "application/pdf");
        var pdf = await SendFileAsync(crew.Paula, crew.Group, MessageType.File, file.Id);

        var result = await SendAsync(crew.Paula, new DeleteMessageCommand(pdf.Id));

        result.ShouldSucceed();
    }

    [Fact]
    public async Task GetMessage_Member_ReturnsTheMessage()
    {
        var (vera, walt, _, direct) = await CreateVeraWaltXenaAsync();
        var fromWalt = await SendTextAsync(walt, direct, "unread for vera");

        var message = (await SendAsync(vera, new GetMessageQuery(fromWalt.Id))).ShouldSucceed();

        message.Content.ShouldBe("unread for vera");
        message.Sender.Id.ShouldBe(walt);
    }

    [Fact]
    public async Task GetMessage_NonMember_ReturnsNotMember()
    {
        var (_, walt, xena, direct) = await CreateVeraWaltXenaAsync();
        var fromWalt = await SendTextAsync(walt, direct, "unread for vera");

        var result = await SendAsync(xena, new GetMessageQuery(fromWalt.Id));

        result.ShouldFailWith(ConversationErrors.NotMember);
    }

    [Fact]
    public async Task GetMessage_UnknownMessage_ReturnsNotFound()
    {
        var (vera, _, _, _) = await CreateVeraWaltXenaAsync();

        var result = await SendAsync(vera, new GetMessageQuery(Guid.NewGuid()));

        result.ShouldFailWith(MessageErrors.NotFound);
    }

    [Fact]
    public async Task GetMessage_HiddenByClear_ReturnsNotFoundOnlyForTheMemberWhoCleared()
    {
        var (vera, walt, _, direct) = await CreateVeraWaltXenaAsync();
        var fromWalt = await SendTextAsync(walt, direct, "unread for vera");
        await PauseAsync();

        await ClearChatAsync(vera, direct);

        (await SendAsync(vera, new GetMessageQuery(fromWalt.Id))).ShouldFailWith(MessageErrors.NotFound);
        (await SendAsync(walt, new GetMessageQuery(fromWalt.Id))).ShouldSucceed();
    }

    [Fact]
    public async Task GetMessage_SentBeforeAndAfterTheCallerJoined_OnlyTheLaterOneIsVisible()
    {
        var (vera, walt, xena, _) = await CreateVeraWaltXenaAsync();
        var group = await CreateGroupAsync(vera, "Security", walt);
        var beforeJoin = await SendTextAsync(walt, group, "before xena");
        await PauseAsync();
        await AddMemberAsync(vera, group, xena);

        var afterJoin = await SendTextAsync(walt, group, "after xena");

        (await SendAsync(xena, new GetMessageQuery(beforeJoin.Id))).ShouldFailWith(MessageErrors.NotFound);
        (await SendAsync(xena, new GetMessageQuery(afterJoin.Id))).ShouldSucceed().Content.ShouldBe("after xena");
    }

    [Fact]
    public async Task GetMessage_DeletedMessage_ReturnsItMasked()
    {
        var (vera, walt, xena, _) = await CreateVeraWaltXenaAsync();
        var group = await CreateGroupAsync(vera, "Security", walt, xena);
        var message = await SendTextAsync(walt, group, "after xena");
        (await SendAsync(walt, new DeleteMessageCommand(message.Id))).ShouldSucceed();

        var deleted = (await SendAsync(xena, new GetMessageQuery(message.Id))).ShouldSucceed();

        deleted.IsDeleted.ShouldBeTrue();
        deleted.Content.ShouldBeNull();
    }

    private async Task<(Guid Vera, Guid Walt, Guid Xena, Guid Direct)> CreateVeraWaltXenaAsync()
    {
        var vera = await CreateUserAsync("Vera");
        var walt = await CreateUserAsync("Walt");
        var xena = await CreateUserAsync("Xena");
        return (vera, walt, xena, await CreateDirectAsync(vera, walt));
    }

    private async Task<List<MessageResponse>> AllMessagesAsync(Guid viewer, Guid conversationId)
    {
        var all = new List<MessageResponse>();
        string? cursor = null;
        do
        {
            var page = (await SendAsync(viewer, new GetMessagesQuery(cursor, 2) { ConversationId = conversationId })).ShouldSucceed();
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return all;
    }
}
