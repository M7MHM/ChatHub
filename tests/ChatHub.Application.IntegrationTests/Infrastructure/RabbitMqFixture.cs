using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ChatHub.Contracts;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace ChatHub.Application.IntegrationTests.Infrastructure;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string User = "chathub";
    public const string Password = "chathub-tests";
    private const int AmqpPort = 5672;

    public RabbitMqFixture()
    {
        Port = FreeTcpPort();
        Container = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(User)
            .WithPassword(Password)
            .WithPortBinding(Port, AmqpPort)
            .Build();
    }

    public int Port { get; }

    public RabbitMqContainer Container { get; }

    public IReadOnlyDictionary<string, string?> MessagingSettings(int batchSize = 100) => new Dictionary<string, string?>
    {
        ["Messaging:Enabled"] = "true",
        ["Messaging:Host"] = Container.Hostname,
        ["Messaging:Port"] = Port.ToString(),
        ["Messaging:User"] = User,
        ["Messaging:Password"] = Password,
        ["Messaging:BatchSize"] = batchSize.ToString(),
        ["Messaging:PollIntervalMilliseconds"] = "50",
        ["Messaging:PublishTimeoutSeconds"] = "5",
        ["Messaging:MaxRetryDelaySeconds"] = "2"
    };

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    public Task<IConnection> ConnectAsync() =>
        new ConnectionFactory { HostName = Container.Hostname, Port = Port, UserName = User, Password = Password }
            .CreateConnectionAsync();

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

public sealed class TestQueue : IAsyncDisposable
{
    private readonly RabbitMqFixture _rabbit;
    private readonly bool _durable;
    private IConnection _connection;
    private IChannel _channel;

    private TestQueue(RabbitMqFixture rabbit, bool durable, IConnection connection, IChannel channel, string name)
    {
        _rabbit = rabbit;
        _durable = durable;
        _connection = connection;
        _channel = channel;
        Name = name;
    }

    public string Name { get; }

    public static async Task<TestQueue> BindAsync(RabbitMqFixture rabbit, params string[] routingKeys) =>
        await CreateAsync(rabbit, durable: false, routingKeys);

    public static async Task<TestQueue> BindDurableAsync(RabbitMqFixture rabbit, params string[] routingKeys) =>
        await CreateAsync(rabbit, durable: true, routingKeys);

    public async Task ReconnectAsync()
    {
        await CloseAsync();
        _connection = await _rabbit.ConnectAsync();
        _channel = await _connection.CreateChannelAsync();
    }

    public async Task<uint> CountAsync() => await _channel.MessageCountAsync(Name);

    public async Task<BasicGetResult?> TryGetAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var result = await _channel.BasicGetAsync(Name, autoAck: true);
            if (result is not null || DateTime.UtcNow >= deadline) return result;
            await Task.Delay(25);
        }
    }

    public async Task<BasicGetResult> GetAsync() =>
        await TryGetAsync(TimeSpan.FromSeconds(10)) ?? throw new ShouldAssertException($"No message arrived in {Name}.");

    public async Task<List<BasicGetResult>> TakeAllAsync()
    {
        var results = new List<BasicGetResult>();
        while (await _channel.BasicGetAsync(Name, autoAck: true) is { } result) results.Add(result);
        return results;
    }

    public static T Read<T>(BasicGetResult result) => JsonSerializer.Deserialize<T>(result.Body.Span, MessageJson.Options)!;

    public async ValueTask DisposeAsync()
    {
        if (_durable)
        {
            try
            {
                if (!_channel.IsOpen) await ReconnectAsync();
                await _channel.QueueDeleteAsync(Name);
            }
            catch (Exception)
            {
            }
        }

        await CloseAsync();
    }

    private static async Task<TestQueue> CreateAsync(RabbitMqFixture rabbit, bool durable, string[] routingKeys)
    {
        var connection = await rabbit.ConnectAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(MessageRoutes.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        var name = $"chathub-tests-{Guid.NewGuid():N}";
        await channel.QueueDeclareAsync(name, durable: durable, exclusive: !durable, autoDelete: !durable);
        foreach (var routingKey in routingKeys)
            await channel.QueueBindAsync(name, MessageRoutes.Exchange, routingKey);
        return new TestQueue(rabbit, durable, connection, channel, name);
    }

    private async Task CloseAsync()
    {
        try
        {
            await _channel.DisposeAsync();
            await _connection.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }
}
