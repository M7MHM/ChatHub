using ChatHub.Application.Common.Interfaces;
using ChatHub.Domain.Enums;

namespace ChatHub.Application.Features.Files;

public sealed class FileUrls(IFileUrlSigner signer, TimeProvider timeProvider)
{
    public static readonly TimeSpan SignedUrlLifetime = TimeSpan.FromHours(1);
    private const string PathPrefix = "/api/files/";

    public static string PathFor(Guid fileId) => $"{PathPrefix}{fileId}";

    public static bool TryGetFileId(string? path, out Guid fileId)
    {
        fileId = Guid.Empty;
        return path is not null && path.StartsWith(PathPrefix, StringComparison.Ordinal) &&
               Guid.TryParse(path.AsSpan(PathPrefix.Length), out fileId);
    }

    public string SignedPathFor(Guid fileId) =>
        signer.Sign(fileId, timeProvider.GetUtcNow().UtcDateTime + SignedUrlLifetime);

    public string UrlFor(Guid fileId, FilePurpose purpose) =>
        purpose == FilePurpose.MessageAttachment ? SignedPathFor(fileId) : PathFor(fileId);
}
