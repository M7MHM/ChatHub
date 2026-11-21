using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed class ChatHubApiFactory(
    string connectionString,
    string filesRoot,
    LogCapture logs,
    string? redisConnectionString = null,
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string JwtSecret = "api-tests-jwt-secret-0123456789abcdef0123";
    public const string FileSigningKey = "api-tests-file-signing-key-0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
        builder.UseSetting("FileStorage:RootPath", filesRoot);
        builder.UseSetting("FileStorage:SigningKey", FileSigningKey);
        builder.UseSetting("Jwt:Secret", JwtSecret);
        if (redisConnectionString is not null)
            builder.UseSetting("ConnectionStrings:Redis", redisConnectionString);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(logs);
        });
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>());
    }
}

public sealed class ClientAddressStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Client-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value) && IPAddress.TryParse(value, out var address))
                context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class ClientAddressHandler(string address) : DelegatingHandler
{
    private static int _next;

    public string Address { get; } = address;

    public static string NextAddress()
    {
        var n = Interlocked.Increment(ref _next);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(ClientAddressStartupFilter.HeaderName);
        request.Headers.Add(ClientAddressStartupFilter.HeaderName, Address);
        return base.SendAsync(request, cancellationToken);
    }
}
