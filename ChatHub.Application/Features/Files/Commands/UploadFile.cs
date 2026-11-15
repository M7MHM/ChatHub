using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Files.Commands;

public sealed record UploadFileCommand(FilePurpose Purpose, Stream Content, string FileName, string ContentType, long SizeBytes)
    : IRequest<Result<FileResponse>>;

public sealed class UploadFileCommandValidator : AbstractValidator<UploadFileCommand>
{
    public UploadFileCommandValidator()
    {
        RuleFor(x => x.Purpose).IsInEnum();
        RuleFor(x => x.Content).NotNull().OverridePropertyName("File");
        RuleFor(x => x.SizeBytes).GreaterThan(0).OverridePropertyName("File").WithMessage("The file is empty.");
        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(UploadedFile.MaxContentTypeLength).OverridePropertyName("File");
        RuleFor(x => x.ContentType)
            .Must(type => type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .When(x => x.Purpose is FilePurpose.Avatar or FilePurpose.GroupImage)
            .OverridePropertyName("File")
            .WithMessage("Avatars and group images must be images.");
    }
}

public sealed class UploadFileCommandHandler(
    IApplicationDbContext db,
    ICurrentUserService currentUser,
    IFileStorage storage,
    FileUrls urls) : IRequestHandler<UploadFileCommand, Result<FileResponse>>
{
    public async Task<Result<FileResponse>> Handle(UploadFileCommand request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var contentType = NormalizeContentType(request.ContentType);
        var storageKey = await storage.SaveAsync(request.Content, contentType, cancellationToken);

        UploadedFile file;
        try
        {
            file = UploadedFile.Create(me, request.Purpose, storageKey,
                SanitizeFileName(request.FileName, Path.GetFileName(storageKey)), contentType, request.SizeBytes);
            db.UploadedFiles.Add(file);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await storage.DeleteAsync(storageKey, CancellationToken.None);
            throw;
        }

        return new FileResponse(file.Id, file.FileName, file.ContentType, file.SizeBytes, urls.UrlFor(file.Id, file.Purpose));
    }

    private static string NormalizeContentType(string contentType)
    {
        var separator = contentType.IndexOf(';');
        return (separator >= 0 ? contentType[..separator] : contentType).Trim().ToLowerInvariant();
    }

    private static string SanitizeFileName(string? fileName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fallback;

        var name = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return fallback;
        return name.Length <= UploadedFile.MaxFileNameLength ? name : name[..UploadedFile.MaxFileNameLength];
    }
}

public sealed record FileResponse(Guid Id, string FileName, string ContentType, long SizeBytes, string Url);
