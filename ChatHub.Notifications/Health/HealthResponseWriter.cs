using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ChatHub.Notifications.Health;

public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), description = entry.Value.Description, data = entry.Value.Data })
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
    }
}
