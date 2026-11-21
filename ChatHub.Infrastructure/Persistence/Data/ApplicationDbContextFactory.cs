using ChatHub.Application.Common.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ChatHub.Infrastructure.Persistence.Data;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Development;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        return Create(configuration.GetConnectionString("DefaultConnection"));
    }

    public static ApplicationDbContext Create(string? connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        if (connectionString is null) options.UseNpgsql();
        else options.UseNpgsql(connectionString);

        return new ApplicationDbContext(options.Options, new NoCurrentUser(), new NoOpPublisher(), TimeProvider.System);
    }

    private sealed class NoCurrentUser : ICurrentUserService
    {
        public Guid? UserId => null;
    }

    private sealed class NoOpPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}
