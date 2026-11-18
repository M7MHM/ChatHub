using ChatHub.Domain.Enums;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

public abstract partial class AppTestBase
{
    protected async Task<Crew> CreateCrewAsync()
    {
        var paula = await CreateUserAsync("Paula");
        var quinn = await CreateUserAsync("Quinn");
        var rex = await CreateUserAsync("Rex");
        var sam = await CreateUserAsync("Sam");
        var tess = await CreateUserAsync("Tess");
        var uma = await CreateUserAsync("Uma");
        var direct = await CreateDirectAsync(paula, quinn);
        var group = await CreateGroupAsync(paula, "Crew", quinn, rex);
        return new Crew(paula, quinn, rex, sam, tess, uma, direct, group);
    }

    protected async Task<CrewHistory> CreateCrewHistoryAsync()
    {
        var crew = await CreateCrewAsync();
        var hello = await SendTextAsync(crew.Paula, crew.Group, "  hello crew  ");
        var imageFile = await UploadPngAsync(crew.Quinn, fileName: "a.png");
        var image = await SendFileAsync(crew.Quinn, crew.Group, MessageType.Image, imageFile.Id, "look");
        var voiceFile = await UploadAsync(crew.Rex, FilePurpose.MessageAttachment, TestFiles.Ogg, "v.ogg", "audio/ogg");
        var voice = await SendFileAsync(crew.Rex, crew.Group, MessageType.Voice, voiceFile.Id);
        var pdfFile = await UploadAsync(crew.Paula, FilePurpose.MessageAttachment, TestFiles.Pdf, "r.pdf", "application/pdf");
        var pdf = await SendFileAsync(crew.Paula, crew.Group, MessageType.File, pdfFile.Id);
        await PauseAsync();
        await ClearChatAsync(crew.Rex, crew.Group);
        await PauseAsync();
        var reply = await SendTextAsync(crew.Quinn, crew.Group, "hi paula", hello.Id);
        await PauseAsync();
        await AddMemberAsync(crew.Paula, crew.Group, crew.Tess);
        await PauseAsync();
        var lateReply = await SendTextAsync(crew.Quinn, crew.Group, "re: hello", hello.Id);
        var tessHi = await SendTextAsync(crew.Tess, crew.Group, "hi all");

        return new CrewHistory(crew, hello.Id, image.Id, voice.Id, pdf.Id, reply.Id, lateReply.Id, tessHi.Id);
    }

    protected sealed record Crew(Guid Paula, Guid Quinn, Guid Rex, Guid Sam, Guid Tess, Guid Uma, Guid Direct, Guid Group);

    protected sealed record CrewHistory(
        Crew Crew, Guid Hello, Guid Image, Guid Voice, Guid Pdf, Guid Reply, Guid LateReply, Guid TessHi);
}
