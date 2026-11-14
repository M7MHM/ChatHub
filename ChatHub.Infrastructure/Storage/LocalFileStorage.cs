using System.Buffers;
using System.Globalization;
using ChatHub.Application.Common.Exceptions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using FluentValidation.Results;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChatHub.Infrastructure.Storage;

public class LocalFileStorage : IFileStorage
{
    private const string UploadsFolder = "uploads";
    private const string UrlPrefix = "/" + UploadsFolder + "/";
    private const int MaxFileNameLength = 255;
    private const int BufferSize = 81920;

    private readonly long _maxFileSizeBytes;
    private readonly TimeProvider _timeProvider;
    private readonly string _uploadsRoot;
    private readonly HashSet<string> _allowedContentTypes;

    public LocalFileStorage(IOptions<FileStorageSettings> options, IHostEnvironment environment, TimeProvider timeProvider)
    {
        var settings = options.Value;
        _maxFileSizeBytes = settings.MaxFileSizeBytes;
        _timeProvider = timeProvider;
        _uploadsRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, settings.RootPath, UploadsFolder));
        _allowedContentTypes = new HashSet<string>(
            settings.AllowedContentTypes.Count > 0 ? settings.AllowedContentTypes : FileStorageSettings.SupportedContentTypes.Keys,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalizedType = NormalizeContentType(contentType);
        if (normalizedType is null || !_allowedContentTypes.Contains(normalizedType) ||
            !FileStorageSettings.SupportedContentTypes.TryGetValue(normalizedType, out var extension))
            throw Invalid($"Files of type '{contentType}' are not allowed.");
        if (content.CanSeek && content.Length - content.Position > _maxFileSizeBytes)
            throw TooLarge();

        var now = _timeProvider.GetUtcNow();
        var year = now.Year.ToString("D4", CultureInfo.InvariantCulture);
        var month = now.Month.ToString("D2", CultureInfo.InvariantCulture);
        var storedName = $"{Guid.CreateVersion7():N}{extension}";

        var directory = Path.Combine(_uploadsRoot, year, month);
        Directory.CreateDirectory(directory);
        var fullPath = Path.Combine(directory, storedName);

        long size;
        try
        {
            await using (var target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous))
            {
                size = await CopyWithLimitAsync(content, target, cancellationToken);
            }

            if (size == 0) throw Invalid("The file is empty.");
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }

        return new StoredFile($"{UrlPrefix}{year}/{month}/{storedName}",
            SanitizeFileName(fileName, storedName), normalizedType, size);
    }

    public Task DeleteAsync(string fileUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(fileUrl) || !fileUrl.StartsWith(UrlPrefix, StringComparison.Ordinal))
            return Task.CompletedTask;

        var relativePath = fileUrl[UrlPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_uploadsRoot, relativePath));

        // Guards against "../" segments escaping the uploads folder.
        if (fullPath.StartsWith(_uploadsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            File.Delete(fullPath);

        return Task.CompletedTask;
    }

    private async Task<long> CopyWithLimitAsync(Stream source, Stream target, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                total += read;
                if (total > _maxFileSizeBytes) throw TooLarge();
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return total;
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

    private static string SanitizeFileName(string? fileName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fallback;

        var name = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return fallback;
        return name.Length <= MaxFileNameLength ? name : name[..MaxFileNameLength];
    }

    private ValidationException TooLarge() =>
        Invalid($"The file exceeds the maximum size of {_maxFileSizeBytes / 1024d / 1024:0.##} MB.");

    private static ValidationException Invalid(string message) => new([new ValidationFailure("File", message)]);
}
