using ChatHub.Contracts;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed class RabbitMqApiFixture : ApiFixture
{
    private const string User = "chathub";
    private const string Password = "chathub-tests";
    private const int AmqpPort = 5672;

    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername(User)
        .WithPassword(Password)
        .Build();

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Messaging:Enabled"] = "true",
        ["Messaging:Host"] = Rabbit.Hostname,
        ["Messaging:Port"] = Rabbit.GetMappedPublicPort(AmqpPort).ToString(),
        ["Messaging:User"] = User,
        ["Messaging:Password"] = Password,
        ["Messaging:PollIntervalMilliseconds"] = "50"
    };

    public override async Task InitializeAsync()
    {
        await Rabbit.StartAsync();
        await base.InitializeAsync();
    }

    protected override async Task DisposeContainersAsync()
    {
        try
        {
            await base.DisposeContainersAsync();
        }
        finally
        {
            await Rabbit.DisposeAsync();
        }
    }

    public async Task<BoundQueue> BindQueueAsync(params string[] routingKeys)
    {
        var connection = await new ConnectionFactory
        {
            HostName = Rabbit.Hostname,
            Port = Rabbit.GetMappedPublicPort(AmqpPort),
            UserName = User,
            Password = Password
        }.CreateConnectionAsync();
        var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(MessageRoutes.Exchange, ExchangeType.Topic, durable: true, autoDelete: false);
        var queue = (await channel.QueueDeclareAsync(string.Empty, durable: false, exclusive: true, autoDelete: true)).QueueName;
        foreach (var routingKey in routingKeys)
            await channel.QueueBindAsync(queue, MessageRoutes.Exchange, routingKey);
        return new BoundQueue(connection, channel, queue);
    }
}

public sealed record BoundQueue(IConnection Connection, IChannel Channel, string Name) : IAsyncDisposable
{
    public async Task<BasicGetResult> GetAsync()
    {
        BasicGetResult? result = null;
        await Eventually.UntilAsync(async () => (result = await Channel.BasicGetAsync(Name, autoAck: true)) is not null,
            $"No message arrived in {Name}.");
        return result!;
    }

    public async ValueTask DisposeAsync()
    {
        await Channel.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RabbitMqApiCollection : ICollectionFixture<RabbitMqApiFixture>
{
    public const string Name = "Api with RabbitMQ";
}
