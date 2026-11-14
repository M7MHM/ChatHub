using ChatHub.Application.Common.Models;

namespace ChatHub.Application.Common.Interfaces;

public interface IFileStorage
{
    Task<StoredFile> SaveAsync(Stream content, string fileName, string contentType,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string fileUrl, CancellationToken cancellationToken = default);
}
