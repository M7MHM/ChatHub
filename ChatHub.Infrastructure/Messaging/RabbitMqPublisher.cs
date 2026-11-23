using System.Text;
using ChatHub.Contracts;
using ChatHub.Infrastructure.RealTime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ChatHub.Infrastructure.Messaging;

public sealed record OutboxEnvelope(Guid Id, string Type, string RoutingKey, string Payload, DateTime OccurredAtUtc, int Attempts);

public sealed record PublishFailure(Guid Id, string Error);

public sealed class RabbitMqPublisher(
    IOptions<MessagingSettings> options,
    ServerInstance instance,
    ILogger<RabbitMqPublisher> logger) : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public bool IsConnected => _connection is { IsOpen: true } && _channel is { IsOpen: true };

    public string? LastConnectionError { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await OpenChannelAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PublishFailure>> PublishAsync(IReadOnlyList<OutboxEnvelope> messages,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            IChannel channel;
            try
            {
                channel = await OpenChannelAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return [.. messages.Select(m => new PublishFailure(m.Id, $"RabbitMQ is unreachable: {exception.Message}"))];
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.PublishTimeout);

            var confirms = new List<(Guid Id, ValueTask Confirm)>(messages.Count);
            foreach (var message in messages)
                confirms.Add((message.Id, channel.BasicPublishAsync(MessageRoutes.Exchange, message.RoutingKey, mandatory: true,
                    PropertiesOf(message), Encoding.UTF8.GetBytes(message.Payload), timeout.Token)));

            var failures = new List<PublishFailure>();
            var channelSuspect = false;
            foreach (var (id, confirm) in confirms)
            {
                try
                {
                    await confirm;
                }
                catch (PublishReturnException returned)
                {
                    failures.Add(new PublishFailure(id,
                        $"Unroutable: no queue is bound to '{returned.Exchange}' for '{returned.RoutingKey}' ({returned.ReplyCode} {returned.ReplyText})."));
                }
                catch (PublishException)
                {
                    failures.Add(new PublishFailure(id, "The broker rejected the message (nack)."));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failures.Add(new PublishFailure(id, $"No publisher confirm within {options.Value.PublishTimeout.TotalSeconds:0} s."));
                    channelSuspect = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add(new PublishFailure(id, exception.Message));
                    channelSuspect = true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (channelSuspect || !IsConnected) await CloseAsync();
            return failures;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _gate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<IChannel> OpenChannelAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true }) return _channel;

        if (_connection is not null)
            logger.LogWarning("The RabbitMQ connection was closed ({Reason}); reconnecting.",
                _channel?.CloseReason?.ReplyText ?? _connection.CloseReason?.ReplyText ?? "unknown reason");
        await CloseAsync();

        var settings = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            UserName = settings.User,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
            ClientProvidedName = $"chathub-api {instance.Id} outbox",
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            RequestedConnectionTimeout = ConnectionTimeout
        };

        try
        {
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true,
                    outstandingPublisherConfirmationsRateLimiter: null),
                cancellationToken);
            await _channel.ExchangeDeclareAsync(MessageRoutes.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            LastConnectionError = exception.Message;
            await CloseAsync();
            throw;
        }

        LastConnectionError = null;
        logger.LogInformation("Connected to RabbitMQ at {Host}:{Port}{VirtualHost}; publishing to exchange {Exchange}.",
            settings.Host, settings.Port, settings.VirtualHost, MessageRoutes.Exchange);
        return _channel;
    }

    private static BasicProperties PropertiesOf(OutboxEnvelope message) => new()
    {
        MessageId = message.Id.ToString(),
        Type = message.Type,
        ContentType = MessageJson.ContentType,
        ContentEncoding = "utf-8",
        Persistent = true,
        Timestamp = new AmqpTimestamp(new DateTimeOffset(DateTime.SpecifyKind(message.OccurredAtUtc, DateTimeKind.Utc))
            .ToUnixTimeSeconds())
    };

    private async Task CloseAsync()
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        try
        {
            if (channel is not null) await channel.DisposeAsync();
            if (connection is not null) await connection.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Closing the RabbitMQ connection failed.");
        }
    }
}
