using ChatHub.Infrastructure.RealTime;

namespace ChatHub.WebAPI.Extensions;

public static class InstanceIdHeaderExtensions
{
    public const string HeaderName = "X-Instance-Id";
    public const string SettingName = "Diagnostics:ExposeInstanceId";

    public static WebApplication UseInstanceIdHeader(this WebApplication app)
    {
        if (!app.Configuration.GetValue(SettingName, false)) return app;

        var instanceId = app.Services.GetRequiredService<ServerInstance>().Id;
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[HeaderName] = instanceId;
                return Task.CompletedTask;
            });
            return next(context);
        });
        return app;
    }
}
