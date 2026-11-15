using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Files;

internal static class UploadedFileAccess
{
    public static async Task<Result<UploadedFile>> LoadAttachableFileAsync(
        this IApplicationDbContext db, Guid fileId, Guid ownerId, FilePurpose purpose, CancellationToken cancellationToken)
    {
        var file = await db.UploadedFiles.FirstOrDefaultAsync(f => f.Id == fileId && !f.IsDeleted, cancellationToken);
        if (file is null) return FileErrors.NotFound;
        if (file.OwnerId != ownerId) return FileErrors.NotOwned;
        if (file.Purpose != purpose) return FileErrors.WrongPurpose;
        if (file.AttachedAtUtc is not null) return FileErrors.AlreadyAttached;
        return file;
    }

    public static async Task MarkFileDeletedAsync(
        this IApplicationDbContext db, string? fileUrl, FilePurpose purpose, CancellationToken cancellationToken)
    {
        if (!FileUrls.TryGetFileId(fileUrl, out var fileId)) return;

        var file = await db.UploadedFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.Purpose == purpose, cancellationToken);
        file?.MarkDeleted();
    }
}
