using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.WebAPI.Filters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ChatHub.WebAPI.Controllers;

public sealed class FilesController(ISender sender, IFileStorage fileStorage) : ApiController(sender)
{
    [HttpPost("files")]
    [Consumes("multipart/form-data")]
    [FileUploadLimits]
    [ProducesResponseType<StoredFile>(StatusCodes.Status201Created)]
    public async Task<ActionResult<StoredFile>> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            ModelState.AddModelError("file", "A file is required.");
            return ValidationProblem(ModelState);
        }

        await using var content = file.OpenReadStream();
        var stored = await fileStorage.SaveAsync(content, file.FileName, file.ContentType, cancellationToken);
        return Created(stored.Url, stored);
    }
}
