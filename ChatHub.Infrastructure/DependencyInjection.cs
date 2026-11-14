using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Infrastructure.RealTime;
using ChatHub.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ChatHub.Infrastructure;

public static class DependencyInjection
{
    public const string ChatHubPath = "/hubs/chat";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddPersistence(configuration);
        services.AddSecurity(configuration);
        services.AddFileStorage(configuration);
        services.AddRealTime();

        return services;
    }

    private static void AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
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
                    }
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
            .ValidateOnStart();

        services.AddSingleton<IFileStorage, LocalFileStorage>();
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
