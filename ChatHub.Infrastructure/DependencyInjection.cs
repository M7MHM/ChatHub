using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatHub.Application.Common.Caching;
using ChatHub.Application.Common.Emails;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Features.Auth;
using ChatHub.Infrastructure.Caching;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Messaging;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Infrastructure.RealTime;
using ChatHub.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace ChatHub.Infrastructure;

public static class DependencyInjection
{
    public const string ChatHubPath = "/hubs/chat";
    public const string CachingEnabledSetting = "Caching:Enabled";
    private const string RedisInstanceName = "chathub:";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        var redis = RedisConfiguration(configuration);
        var messaging = MessagingEnabled(configuration);

        services.AddCaching(configuration, redis);
        services.AddPersistence(configuration, messaging);
        services.AddSecurity(configuration);
        services.AddAccountEmails(configuration);
        services.AddFileStorage(configuration);
        services.AddRealTime(configuration, redis);
        if (messaging) services.AddMessaging(configuration);

        return services;
    }

    public static IHealthChecksBuilder AddRedisHealthCheck(this IHealthChecksBuilder builder, IConfiguration configuration,
        params string[] tags) =>
        RedisConfiguration(configuration) is null
            ? builder
            : builder.AddCheck<RedisHealthCheck>("redis", HealthStatus.Degraded, tags);

    public static IHealthChecksBuilder AddMessagingHealthChecks(this IHealthChecksBuilder builder, IConfiguration configuration,
        params string[] tags) =>
        !MessagingEnabled(configuration)
            ? builder
            : builder
                .AddCheck<RabbitMqHealthCheck>("rabbitmq", HealthStatus.Degraded, tags)
                .AddCheck<OutboxBacklogHealthCheck>("outbox", HealthStatus.Degraded, tags);

    private static bool MessagingEnabled(IConfiguration configuration) =>
        configuration.GetValue(MessagingSettings.EnabledSetting, false);

    private static void AddPersistence(this IServiceCollection services, IConfiguration configuration, bool outbox)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

        services.AddScoped<CacheInvalidationInterceptor>();
        if (outbox) services.AddScoped<OutboxInterceptor>();
        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
            if (outbox) options.AddInterceptors(sp.GetRequiredService<OutboxInterceptor>());
            options.AddInterceptors(sp.GetRequiredService<CacheInvalidationInterceptor>());
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
    }

    private static void AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MessagingSettings>()
            .Bind(configuration.GetSection(MessagingSettings.SectionName))
            .Validate(s => !string.IsNullOrWhiteSpace(s.Host) && s.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(s.VirtualHost),
                "Messaging requires a Host, a Port between 1 and 65535 and a VirtualHost.")
            .Validate(s => !string.IsNullOrWhiteSpace(s.User) && !string.IsNullOrEmpty(s.Password),
                "Messaging requires a User and a Password.")
            .Validate(s => s.BatchSize is > 0 and <= 1000 && s.PollIntervalMilliseconds > 0,
                "Messaging BatchSize must be between 1 and 1000 and PollIntervalMilliseconds greater than zero.")
            .Validate(s => s.RetentionDays > 0 && s.CleanupIntervalMinutes > 0,
                "Messaging RetentionDays and CleanupIntervalMinutes must be greater than zero.")
            .Validate(s => s.PublishTimeoutSeconds > 0 && s.MaxRetryDelaySeconds > 0 && s.BacklogAlertMinutes > 0,
                "Messaging PublishTimeoutSeconds, MaxRetryDelaySeconds and BacklogAlertMinutes must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<RabbitMqPublisher>();
        services.AddSingleton<OutboxPublisherService>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxPublisherService>());
    }

    private static void AddCaching(this IServiceCollection services, IConfiguration configuration, ConfigurationOptions? redis)
    {
        if (redis is not null)
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis.Clone()));

        if (!configuration.GetValue(CachingEnabledSetting, true))
        {
            services.AddSingleton<HybridCache, PassThroughHybridCache>();
            return;
        }

        services.AddHybridCache(options => options.DefaultEntryOptions = CacheKeys.DefaultEntryOptions);

        if (redis is null) return;

        services.AddStackExchangeRedisCache(options =>
        {
            options.InstanceName = RedisInstanceName;
            options.ConfigurationOptions = redis.Clone();
        });
    }

    private static ConfigurationOptions? RedisConfiguration(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ChannelPrefix = RedisChannel.Literal(RedisInstanceName);
        return options;
    }

    private static void AddSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection(JwtSettings.SectionName))
            .Validate(s => Encoding.UTF8.GetByteCount(s.Secret) >= 32
                           && !string.IsNullOrWhiteSpace(s.Issuer)
                           && !string.IsNullOrWhiteSpace(s.Audience),
                "Jwt settings require a Secret of at least 32 bytes, an Issuer and an Audience.")
            .Validate(s => s.ExpiryMinutes > 0 && s.RefreshTokenDays > 0,
                "Jwt ExpiryMinutes and RefreshTokenDays must be greater than zero.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtSettings>>((options, jwt) =>
            {
                var settings = jwt.Value;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Secret)),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name"
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) &&
                            context.HttpContext.Request.Path.StartsWithSegments(ChatHubPath))
                            context.Token = accessToken;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = SecurityStampValidator.ValidateAsync
                };
            });

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddSingleton<IIdentityService, IdentityService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
    }

    private static void AddAccountEmails(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AppUrlSettings>()
            .Bind(configuration.GetSection(AppUrlSettings.SectionName))
            .Validate(s => AppUrlSettings.IsValidBaseUrl(s.PublicApiBaseUrl),
                "App PublicApiBaseUrl must be an absolute http(s) URL without a query string.")
            .ValidateOnStart();

        services.AddOptions<EmailVerificationSettings>()
            .Bind(configuration.GetSection(EmailVerificationSettings.SectionName))
            .Validate(s => s.TokenLifetimeHours is > 0 and <= 168,
                "EmailVerification TokenLifetimeHours must be between 1 and 168.")
            .Validate(s => s.MaxRequestsPerHour is > 0 and <= 100,
                "EmailVerification MaxRequestsPerHour must be between 1 and 100.")
            .ValidateOnStart();

        services.AddSingleton(sp => new EmailLinks(sp.GetRequiredService<IOptions<AppUrlSettings>>().Value.PublicApiBaseUrl));
    }

    private static void AddFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FileStorageSettings>()
            .Bind(configuration.GetSection(FileStorageSettings.SectionName))
            .Validate(s => !string.IsNullOrWhiteSpace(s.RootPath) && s.MaxFileSizeBytes > 0,
                "FileStorage requires a RootPath and a MaxFileSizeBytes greater than zero.")
            .Validate(s => s.AllowedContentTypes.All(FileStorageSettings.SupportedContentTypes.ContainsKey),
                "FileStorage AllowedContentTypes may only contain supported content types.")
            .Validate(s => Encoding.UTF8.GetByteCount(s.SigningKey) >= 32,
                "FileStorage requires a SigningKey of at least 32 bytes.")
            .Validate(s => s.CleanupIntervalMinutes > 0,
                "FileStorage CleanupIntervalMinutes must be greater than zero.")
            .ValidateOnStart();

        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IFileUrlSigner, HmacFileUrlSigner>();
        services.AddHostedService<FileCleanupService>();
    }

    private static void AddRealTime(this IServiceCollection services, IConfiguration configuration, ConfigurationOptions? redis)
    {
        var signalR = services.AddSignalR().AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();
        services.AddSingleton(ServerInstance.Create());
        services.AddSingleton<PresenceSynchronizer>();
        services.AddScoped<ISignalRService, SignalRService>();

        if (redis is null)
        {
            services.AddSingleton<IUserConnectionTracker, InMemoryUserConnectionTracker>();
            return;
        }

        signalR.AddStackExchangeRedis(options => options.Configuration = redis.Clone());

        services.AddOptions<RealTimeSettings>()
            .Bind(configuration.GetSection(RealTimeSettings.SectionName))
            .Validate(s => s.HeartbeatIntervalSeconds > 0 && s.InstanceTimeoutSeconds >= 2 * s.HeartbeatIntervalSeconds,
                "RealTime requires a HeartbeatIntervalSeconds greater than zero and an InstanceTimeoutSeconds of at least twice that.")
            .ValidateOnStart();

        services.AddSingleton<RedisUserConnectionTracker>();
        services.AddSingleton<IUserConnectionTracker>(sp => sp.GetRequiredService<RedisUserConnectionTracker>());
        services.AddHostedService<RedisPresenceMaintenance>();
    }
}
