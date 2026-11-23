using ChatHub.Notifications;
using ChatHub.Notifications.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddNotificationsService(builder.Configuration);

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag),
    ResponseWriter = HealthResponseWriter.WriteAsync
});

app.Run();

public partial class Program;
