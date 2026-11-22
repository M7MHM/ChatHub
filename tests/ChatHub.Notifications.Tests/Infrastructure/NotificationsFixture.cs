using ChatHub.Notifications.Messaging;
using ChatHub.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using Respawn;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ChatHub.Notifications.Tests.Infrastructure;

public sealed class NotificationsFixture : IAsyncLifetime
{
    public const string RabbitUser = "chathub";
    public const string RabbitPassword = "chathub-tests";
    private const int AmqpPort = 5672;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private Respawner? _respawner;

    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername(RabbitUser)
        .WithPassword(RabbitPassword)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public int RabbitPort => Rabbit.GetMappedPublicPort(AmqpPort);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), Rabbit.StartAsync());
        ConnectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Database = "chathub_notifications"
        }.ConnectionString;

        await using (var db = new NotificationsDbContext(
                         new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(ConnectionString).Options))
            await db.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = ["__EFMigrationsHistory"]
        });
    }

    public async Task ResetAsync()
    {
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await _respawner!.ResetAsync(connection);
        }

        await using var rabbit = await ConnectAsync();
        await using var channel = await rabbit.CreateChannelAsync();
        await NotificationTopology.DeclareAsync(channel, CancellationToken.None);
        foreach (var queue in new[] { NotificationTopology.Queue, NotificationTopology.RetryQueue, NotificationTopology.DeadLetterQueue })
            await channel.QueuePurgeAsync(queue);
    }

    public Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { HostName = Rabbit.Hostname, Port = RabbitPort, UserName = RabbitUser, Password = RabbitPassword }
            .CreateConnectionAsync();

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await Rabbit.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class NotificationsCollection : ICollectionFixture<NotificationsFixture>
{
    public const string Name = "Notifications";
}
