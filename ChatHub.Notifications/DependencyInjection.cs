using ChatHub.Notifications.Channels;
using ChatHub.Notifications.Channels.Email;
using ChatHub.Notifications.Digests;
using ChatHub.Notifications.Health;
using ChatHub.Notifications.Messaging;
using ChatHub.Notifications.Persistence;
using ChatHub.Notifications.Sending;
using ChatHub.Notifications.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ChatHub.Notifications;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddNotificationsService(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<NotificationSettings>()
            .Bind(configuration.GetSection(NotificationSettings.SectionName))
            .Validate(s => s.DigestDelaySeconds > 0 && s.CooldownMinutes >= 0,
                "Notifications DigestDelaySeconds must be greater than zero and CooldownMinutes not negative.")
            .Validate(s => Uri.TryCreate(s.AppBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https",
                "Notifications AppBaseUrl must be an absolute http(s) URL.")
            .Validate(s => s.MaxAttempts is > 0 and <= 20 && s.RetryBaseDelaySeconds > 0,
                "Notifications MaxAttempts must be between 1 and 20 and RetryBaseDelaySeconds greater than zero.")
            .Validate(s => s.PollIntervalMilliseconds > 0 && s.BatchSize is > 0 and <= 500,
                "Notifications PollIntervalMilliseconds must be greater than zero and BatchSize between 1 and 500.")
            .Validate(s => s.MaxMessageAgeHours > 0 && s.InboxRetention > s.MaxMessageAge,
                "Notifications InboxRetentionDays must cover MaxMessageAgeHours.")
            .Validate(s => s.HoldRecheckSeconds > 0 && s.SendBacklogAlertMinutes > 0,
                "Notifications HoldRecheckSeconds and SendBacklogAlertMinutes must be greater than zero.")
            .Validate(s => s.MaxEmailsPerMinute > 0 && s.MaxDigestsPerMinute is > 0 && s.MaxDigestsPerMinute <= s.MaxEmailsPerMinute,
                "Notifications MaxEmailsPerMinute must be greater than zero and MaxDigestsPerMinute between 1 and MaxEmailsPerMinute.")
            .Validate(s => s.MaxEmailsPerDay > 0 && s.MaxDigestsPerDay > 0 && s.MaxDigestsPerDay <= s.MaxEmailsPerDay,
                "Notifications MaxEmailsPerDay must be greater than zero and MaxDigestsPerDay between 1 and MaxEmailsPerDay.")
            .Validate(s => s.MaxEmailsPerRecipientPerHour > 0,
                "Notifications MaxEmailsPerRecipientPerHour must be greater than zero.")
            .Validate<IOptions<SmtpSettings>>((s, smtp) => s.Lease >= 2 * smtp.Value.Timeout,
                "Notifications LeaseSeconds must be at least twice Smtp TimeoutSeconds.")
            .ValidateOnStart();

        services.AddOptions<RabbitMqSettings>()
            .Bind(configuration.GetSection(RabbitMqSettings.SectionName))
            .Validate(s => !string.IsNullOrWhiteSpace(s.Host) && s.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(s.VirtualHost),
                "RabbitMq requires a Host, a Port between 1 and 65535 and a VirtualHost.")
            .Validate(s => !string.IsNullOrWhiteSpace(s.User) && !string.IsNullOrEmpty(s.Password),
                "RabbitMq requires a User and a Password.")
            .Validate(s => s.PrefetchCount > 0 && s.MaxDeliveryAttempts is > 0 and <= 20 && s.RetryDelayMilliseconds > 0,
                "RabbitMq PrefetchCount and RetryDelayMilliseconds must be greater than zero and MaxDeliveryAttempts between 1 and 20.")
            .ValidateOnStart();

        services.AddOptions<SmtpSettings>()
            .Bind(configuration.GetSection(SmtpSettings.SectionName))
            .Validate(s => !string.IsNullOrWhiteSpace(s.Host) && s.Port is > 0 and <= 65535 && s.TimeoutSeconds > 0,
                "Smtp requires a Host, a Port between 1 and 65535 and a TimeoutSeconds greater than zero.")
            .Validate(s => MailboxAddress.TryParse(s.FromAddress, out var from) && from.Address.Contains('@') &&
                           !string.IsNullOrWhiteSpace(s.FromName),
                "Smtp requires a valid FromAddress and a FromName.")
            .Validate(s => string.IsNullOrEmpty(s.User) || !string.IsNullOrEmpty(s.Password),
                "Smtp Password is required when User is set.")
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString(NotificationsDbContext.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{NotificationsDbContext.ConnectionStringName}' was not found.");
        services.AddDbContext<NotificationsDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<MessageSentHandler>();
        services.AddScoped<ConversationReadHandler>();
        services.AddScoped<UserEventsHandler>();
        services.AddScoped<EmailVerificationHandler>();
        services.AddScoped<ConversationEventsHandler>();
        services.AddScoped<PresenceHandler>();
        services.AddSingleton<SendQuota>();
        services.AddSingleton<EmailSuppression>();
        services.AddScoped<IIntegrationEventProcessor, IntegrationEventProcessor>();
        services.AddSingleton<INotificationChannel, EmailChannel>();

        services.AddSingleton<EventConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<EventConsumer>());
        services.AddSingleton<DigestDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<DigestDispatcher>());
        services.AddSingleton<RetentionCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<RetentionCleanupService>());

        services.AddHealthChecks()
            .AddDbContextCheck<NotificationsDbContext>("database", tags: [ReadyTag])
            .AddCheck<RabbitMqConsumerHealthCheck>("rabbitmq", tags: [ReadyTag])
            .AddCheck<SendingHealthCheck>("sending", HealthStatus.Degraded, [ReadyTag]);

        return services;
    }
}
