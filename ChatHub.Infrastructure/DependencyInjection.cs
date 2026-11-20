using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatHub.Application.Common.Caching;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Infrastructure.Caching;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Infrastructure.RealTime;
using ChatHub.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
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
    private const string RedisInstanceName = "chathub:";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddCaching(configuration);
        services.AddPersistence(configuration);
        services.AddSecurity(configuration);
        services.AddFileStorage(configuration);
        services.AddRealTime();

        return services;
    }

    public static IHealthChecksBuilder AddRedisHealthCheck(this IHealthChecksBuilder builder, IConfiguration configuration,
        params string[] tags) =>
        RedisConfiguration(configuration) is null
            ? builder
            : builder.AddCheck<RedisHealthCheck>("redis", HealthStatus.Degraded, tags);

    private static void AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

        services.AddScoped<CacheInvalidationInterceptor>();
        services.AddDbContext<ApplicationDbContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName))
            .AddInterceptors(sp.GetRequiredService<CacheInvalidationInterceptor>()));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
    }

    private static void AddCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHybridCache(options => options.DefaultEntryOptions = CacheKeys.DefaultEntryOptions);

        if (RedisConfiguration(configuration) is not { } redis) return;

        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
        services.AddStackExchangeRedisCache(options => options.InstanceName = RedisInstanceName);
        services.AddOptions<RedisCacheOptions>()
            .Configure<IServiceProvider>((options, provider) => options.ConnectionMultiplexerFactory =
                () => Task.FromResult(provider.GetRequiredService<IConnectionMultiplexer>()));
    }

    private static ConfigurationOptions? RedisConfiguration(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
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

    private static void AddRealTime(this IServiceCollection services)
    {
        services.AddSignalR().AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        services.AddSingleton<IUserIdProvider, SubClaimUserIdProvider>();
        services.AddSingleton<UserConnectionTracker>();
        services.AddScoped<ISignalRService, SignalRService>();
    }
}
