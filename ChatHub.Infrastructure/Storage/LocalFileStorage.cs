using System.Buffers;
using System.Globalization;
using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Common.Interfaces;
using FluentValidation.Results;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Storage;

public class LocalFileStorage : IFileStorage
{
    private const int BufferSize = 81920;

    private readonly long _maxFileSizeBytes;
    private readonly TimeProvider _timeProvider;
    private readonly string _root;
    private readonly HashSet<string> _allowedContentTypes;

    public LocalFileStorage(IOptions<FileStorageSettings> options, IHostEnvironment environment, TimeProvider timeProvider)
    {
        var settings = options.Value;
        _maxFileSizeBytes = settings.MaxFileSizeBytes;
        _timeProvider = timeProvider;
        _root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, settings.RootPath));
        _allowedContentTypes = new HashSet<string>(
            settings.AllowedContentTypes.Count > 0 ? settings.AllowedContentTypes : FileStorageSettings.SupportedContentTypes.Keys,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedType = NormalizeContentType(contentType);
        if (normalizedType is null || !_allowedContentTypes.Contains(normalizedType) ||
            !FileStorageSettings.SupportedContentTypes.TryGetValue(normalizedType, out var extension))
            throw Invalid($"Files of type '{contentType}' are not allowed.");
        if (content.CanSeek && content.Length - content.Position > _maxFileSizeBytes)
            throw TooLarge();

        var header = new byte[FileSignatures.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (headerLength == 0) throw Invalid("The file is empty.");
        if (!FileSignatures.Matches(normalizedType, header.AsMemory(0, headerLength)))
            throw Invalid("File content does not match its type");

        var now = _timeProvider.GetUtcNow();
        var storageKey = string.Create(CultureInfo.InvariantCulture,
            $"{now.Year:D4}/{now.Month:D2}/{Guid.CreateVersion7():N}{extension}");
        var fullPath = ResolvePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        try
        {
            await using var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous);
            await target.WriteAsync(header.AsMemory(0, headerLength), cancellationToken);
            await CopyWithLimitAsync(content, target, headerLength, cancellationToken);
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }

        return storageKey;
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(ResolvePath(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ResolvePath(storageKey));
        return Task.CompletedTask;
    }

    private string ResolvePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            throw new ArgumentException("A storage key is required.", nameof(storageKey));

        var fullPath = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The storage key points outside the storage root.", nameof(storageKey));
        return fullPath;
    }

    private async Task CopyWithLimitAsync(Stream source, Stream target, long alreadyWritten, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var total = alreadyWritten;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                total += read;
                if (total > _maxFileSizeBytes) throw TooLarge();
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;

        var separator = contentType.IndexOf(';');
        var mediaType = (separator >= 0 ? contentType[..separator] : contentType).Trim().ToLowerInvariant();
        return mediaType.Length == 0 ? null : mediaType;
    }

    private ValidationException TooLarge() =>
        Invalid($"The file exceeds the maximum size of {_maxFileSizeBytes / 1024d / 1024:0.##} MB.");

    private static ValidationException Invalid(string message) => new([new ValidationFailure("File", message)]);
}
