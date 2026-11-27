using ChatHub.Application.Features.Guests.Commands;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatHub.Infrastructure.Guests;

public sealed class DemoPersonaInitializer(IServiceScopeFactory scopeFactory, ILogger<DemoPersonaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var created = await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new EnsureDemoPersonasCommand(), cancellationToken);
            if (created is { IsSuccess: true, Value: > 0 })
                logger.LogInformation("Created {Count} demo personas.", created.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not create the demo personas at startup; the first guest sign-in creates them.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
