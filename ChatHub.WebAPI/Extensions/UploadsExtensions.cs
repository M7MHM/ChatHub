using ChatHub.Infrastructure.Storage;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace ChatHub.WebAPI.Extensions;

public static class UploadsExtensions
{
    private const string UploadsFolder = "uploads";
    private const string RequestPath = "/uploads";

    public static WebApplication UseUploads(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<IOptions<FileStorageSettings>>().Value;
        var root = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, settings.RootPath, UploadsFolder));
        Directory.CreateDirectory(root);

        // Serve exactly the types the storage accepts, under the content type they were uploaded as.
        var contentTypes = FileStorageSettings.SupportedContentTypes
            .GroupBy(type => type.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            RequestPath = RequestPath,
            ContentTypeProvider = new FileExtensionContentTypeProvider(contentTypes),
            OnPrepareResponse = context =>
            {
                var response = context.Context.Response;
                response.Headers.XContentTypeOptions = "nosniff";
                if (!IsInlineType(response.ContentType))
                    response.Headers.ContentDisposition = "attachment";
            }
        });

        return app;
    }

    private static bool IsInlineType(string? contentType) =>
        contentType is not null &&
        (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
         contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
}
