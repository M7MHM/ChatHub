using System.Reflection;
using ChatHub.Application.Common.Behaviors;
using ChatHub.Application.Features.Auth;
using ChatHub.Application.Features.Files;
using ChatHub.Application.Features.Users;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();

        services.AddValidatorsFromAssembly(assembly);
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });

        services.AddScoped<AuthTokenService>();
        services.AddScoped<EmailTokenService>();
        services.AddScoped<AccountDeletion>();
        services.AddSingleton<FileUrls>();

        return services;
    }
}
