using System.Text;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ChatHub.Notifications.Messaging;

public sealed class EventConsumer(
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqSettings> options,
    ILogger<EventConsumer> logger) : BackgroundService
{
    public const string AttemptHeader = "chathub-delivery-attempts";
    public const string RoutingKeyHeader = "chathub-routing-key";

    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecoveryGracePeriod = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private IConnection? _connection;
    private IChannel? _channel;
    private IChannel? _retryPublisher;
    private CancellationToken _stoppingToken;

    public bool IsConnected => _connection is { IsOpen: true } && _channel is { IsOpen: true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        var failuresInARow = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await StartConsumingAsync(stoppingToken);
                    failuresInARow = 0;
                    await WatchAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, failuresInARow++), MaxReconnectDelay.TotalSeconds));
                    logger.LogWarning(exception, "Consuming from RabbitMQ failed {Failures} time(s) in a row; reconnecting in {Delay}.",
                        failuresInARow, delay);
                    await CloseAsync();
                    await Task.Delay(delay, stoppingToken);
                }
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await CloseAsync();
        }
    }

    private async Task StartConsumingAsync(CancellationToken cancellationToken)
    {
        await CloseAsync();
        var settings = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = settings.Host,
            Port = settings.Port,
            UserName = settings.User,
            Password = settings.Password,
            VirtualHost = settings.VirtualHost,
            ClientProvidedName = $"chathub-notifications {Environment.MachineName}",
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(2),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        _retryPublisher = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await NotificationTopology.DeclareAsync(_channel, cancellationToken);
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: settings.PrefetchCount, global: false, cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        await _channel.BasicConsumeAsync(NotificationTopology.Queue, autoAck: false, consumer, cancellationToken);

        logger.LogInformation("Consuming {Queue} from RabbitMQ at {Host}:{Port}{VirtualHost}.",
            NotificationTopology.Queue, settings.Host, settings.Port, settings.VirtualHost);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        DateTime? downSince = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(WatchInterval, cancellationToken);
            if (IsConnected)
            {
                downSince = null;
                continue;
            }

            downSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - downSince > RecoveryGracePeriod)
                throw new InvalidOperationException(
                    $"The RabbitMQ connection did not recover within {RecoveryGracePeriod.TotalSeconds:0} s.");
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;
        var routingKey = StringHeader(delivery.BasicProperties, RoutingKeyHeader) ?? delivery.RoutingKey;
        var attempt = IntHeader(delivery.BasicProperties, AttemptHeader) + 1;

        var read = IntegrationEventReader.Read(routingKey, delivery.BasicProperties.MessageId, delivery.Body.Span);
        if (read.Event is null)
        {
            logger.LogWarning("Dead-lettering message {MessageId} ({RoutingKey}): {Error}",
                delivery.BasicProperties.MessageId, routingKey, read.Error);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IIntegrationEventProcessor>();
            if (!await processor.ProcessAsync(read.Event, _stoppingToken))
                logger.LogDebug("Message {MessageId} was already processed; acknowledging the duplicate.", read.Event.MessageId);
        }
        catch (Exception exception) when (!_stoppingToken.IsCancellationRequested)
        {
            await RetryOrDeadLetterAsync(channel, delivery, routingKey, attempt, exception);
            return;
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
    }

    private async Task RetryOrDeadLetterAsync(IChannel channel, BasicDeliverEventArgs delivery, string routingKey, int attempt,
        Exception exception)
    {
        var settings = options.Value;
        var messageId = delivery.BasicProperties.MessageId;
        if (attempt >= settings.MaxDeliveryAttempts)
        {
            logger.LogError(exception, "Message {MessageId} ({RoutingKey}) failed {Attempts} times; dead-lettering it.",
                messageId, routingKey, attempt);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
            return;
        }

        var delay = settings.RetryDelayFor(attempt);
        logger.LogWarning(exception,
            "Processing message {MessageId} ({RoutingKey}) failed (attempt {Attempt} of {MaxAttempts}); retrying in {Delay}.",
            messageId, routingKey, attempt, settings.MaxDeliveryAttempts, delay);

        var headers = delivery.BasicProperties.Headers is { } existing
            ? new Dictionary<string, object?>(existing)
            : new Dictionary<string, object?>();
        headers[AttemptHeader] = attempt;
        headers[RoutingKeyHeader] = routingKey;
        var properties = new BasicProperties(delivery.BasicProperties)
        {
            Headers = headers,
            Expiration = ((long)delay.TotalMilliseconds).ToString()
        };

        try
        {
            await _retryPublisher!.BasicPublishAsync(string.Empty, NotificationTopology.RetryQueue, mandatory: true, properties,
                delivery.Body, _stoppingToken);
        }
        catch (Exception publishError) when (!_stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(publishError, "Could not schedule the retry of message {MessageId}; requeueing it.", messageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
            return;
        }

        await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
    }

    private async Task CloseAsync()
    {
        var connection = _connection;
        _connection = null;
        _channel = null;
        _retryPublisher = null;
        if (connection is null) return;

        try
        {
            await connection.CloseAsync();
            await connection.DisposeAsync();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Closing the RabbitMQ connection failed.");
        }
    }

    private static string? StringHeader(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers?.TryGetValue(name, out var value) == true
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null
            }
            : null;

    private static int IntHeader(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers?.TryGetValue(name, out var value) == true
            ? value switch
            {
                int number => number,
                long number => (int)number,
                byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var number) => number,
                _ => 0
            }
            : 0;
}
