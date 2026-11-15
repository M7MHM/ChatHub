namespace ChatHub.Application.Common.Interfaces;

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
