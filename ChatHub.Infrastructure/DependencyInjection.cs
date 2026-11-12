using System.Text;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Infrastructure.RealTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found.");

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection(JwtSettings.SectionName))
            .Validate(s => Encoding.UTF8.GetByteCount(s.Secret) >= 32
                           && !string.IsNullOrWhiteSpace(s.Issuer)
                           && !string.IsNullOrWhiteSpace(s.Audience),
                "Jwt settings require a Secret of at least 32 bytes, an Issuer and an Audience.")
            .ValidateOnStart();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IIdentityService, IdentityService>();

        services.AddSignalR();
        services.AddSingleton<UserConnectionTracker>();
        services.AddScoped<ISignalRService, SignalRService>();

        return services;
    }
}
