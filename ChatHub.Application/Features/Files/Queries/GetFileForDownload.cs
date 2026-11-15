using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Conversations;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Files.Queries;

public sealed record GetFileForDownloadQuery(Guid FileId, long? Exp = null, string? Sig = null) : IRequest<Result<FileDownload>>;

public sealed class GetFileForDownloadQueryValidator : AbstractValidator<GetFileForDownloadQuery>
{
    public GetFileForDownloadQueryValidator()
    {
        RuleFor(x => x.FileId).NotEmpty();
    }
}

public sealed class GetFileForDownloadQueryHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IFileUrlSigner signer,
    IFileStorage storage) : IRequestHandler<GetFileForDownloadQuery, Result<FileDownload>>
{
    public async Task<Result<FileDownload>> Handle(GetFileForDownloadQuery request, CancellationToken cancellationToken)
    {
        var file = await db.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Id == request.FileId && !f.IsDeleted)
            .Select(f => new { f.OwnerId, f.Purpose, f.StorageKey, f.FileName, f.ContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (file is null) return FileErrors.NotFound;

        if (file.Purpose == FilePurpose.MessageAttachment &&
            currentUser.UserId != file.OwnerId &&
            !signer.Verify(request.FileId, request.Exp, request.Sig) &&
            !await IsVisibleToMemberAsync(request.FileId, cancellationToken))
            return FileErrors.NotFound;

        var content = await storage.OpenReadAsync(file.StorageKey, cancellationToken);
        return new FileDownload(content, file.ContentType, file.FileName, file.Purpose);
    }

    private async Task<bool> IsVisibleToMemberAsync(Guid fileId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } me) return false;

        return await db.Messages
            .AsNoTracking()
            .Where(x => x.Attachment != null && x.Attachment.FileId == fileId)
            .AnyAsync(x => db.ConversationMembers
                .Where(m => m.ConversationId == x.ConversationId && m.UserId == me && m.IsActive && !m.Conversation.IsDeleted)
                .Select(MemberVisibility.Projection)
                .Any(v => x.CreatedAtUtc > v.VisibleAfterUtc), cancellationToken);
    }
}

public sealed record FileDownload(Stream Content, string ContentType, string FileName, FilePurpose Purpose);
