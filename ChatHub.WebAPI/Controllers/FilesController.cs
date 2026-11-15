using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Files.Commands;
using ChatHub.Application.Features.Files.Queries;
using ChatHub.Domain.Enums;
using ChatHub.WebAPI.Filters;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ChatHub.WebAPI.Controllers;

public sealed class FilesController(ISender sender) : ApiController(sender)
{
    [HttpPost("files")]
    [Consumes("multipart/form-data")]
    [FileUploadLimits]
    [ProducesResponseType<FileResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<FileResponse>> Upload(
        [FromQuery] FilePurpose purpose, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            ModelState.AddModelError("file", "A file is required.");
            return ValidationProblem(ModelState);
        }

        await using var content = file.OpenReadStream();
        var command = new UploadFileCommand(purpose, content, file.FileName, file.ContentType, file.Length);
        return ToCreated(await Sender.Send(command, cancellationToken), uploaded => FileUrls.PathFor(uploaded.Id));
    }

    [AllowAnonymous]
    [HttpGet("files/{id:guid}")]
    [ProducesResponseType<FileStreamResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Download(
        Guid id, [FromQuery] long? exp, [FromQuery] string? sig, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetFileForDownloadQuery(id, exp, sig), cancellationToken);
        if (result.IsFailure) return Problem(result.Error);

        var file = result.Value;
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = file.Purpose == FilePurpose.MessageAttachment ? "private" : "public, max-age=86400";

        var inline = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                     file.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        return inline
            ? File(file.Content, file.ContentType, enableRangeProcessing: true)
            : File(file.Content, file.ContentType, file.FileName);
    }
}
