using ChatHub.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace ChatHub.WebAPI.Filters;
[AttributeUsage(AttributeTargets.Method)]
public sealed class FileUploadLimitsAttribute : Attribute, IResourceFilter
{
    private const long MultipartOverheadBytes = 64 * 1024;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var settings = context.HttpContext.RequestServices.GetRequiredService<IOptions<FileStorageSettings>>().Value;
        var limit = settings.MaxFileSizeBytes + MultipartOverheadBytes;
        var features = context.HttpContext.Features;

        if (features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = limit;

        if (features.Get<IFormFeature>()?.Form is null)
            features.Set<IFormFeature>(new FormFeature(context.HttpContext.Request,
                new FormOptions { MultipartBodyLengthLimit = limit }));
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
