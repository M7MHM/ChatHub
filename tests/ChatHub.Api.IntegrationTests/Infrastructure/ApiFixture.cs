using ChatHub.Application.Common.Interfaces;
using ChatHub.Infrastructure.Persistence.Data;
using MediatR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private Respawner? _respawner;
    private ChatHubApiFactory? _factory;

    public string FilesRoot { get; } = Path.Combine(Path.GetTempPath(), $"chathub-api-tests-{Guid.NewGuid():N}");
    public LogCapture Logs { get; } = new();
    public ChatHubApiFactory Factory => _factory ?? throw new InvalidOperationException("The API is not started.");
    public TestServer Server => Factory.Server;
    public IServiceProvider Services => Factory.Services;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();

        await using (var db = CreateDbContext(connectionString))
            await db.Database.MigrateAsync();

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.Postgres,
                SchemasToInclude = ["public"],
                TablesToIgnore = ["__EFMigrationsHistory"]
            });
        }

        Directory.CreateDirectory(FilesRoot);
        _factory = new ChatHubApiFactory(connectionString, FilesRoot, Logs);
        _ = _factory.Server;
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _container.DisposeAsync();
        try
        {
            if (Directory.Exists(FilesRoot)) Directory.Delete(FilesRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ApplicationDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options,
            new NoUser(), new NoOpPublisher(), TimeProvider.System);

    private sealed class NoUser : ICurrentUserService
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

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "Api";
}
