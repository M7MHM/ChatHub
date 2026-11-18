using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace ChatHub.WebAPI.Extensions;

public static class ForwardedHeadersExtensions
{
    public const string SectionName = "ForwardedHeaders";

    public static IServiceCollection AddTrustedForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        var proxies = Values(section, "KnownProxies")
            .Select(value => IPAddress.TryParse(value, out var address) ? address : throw Invalid("KnownProxies", value, "an IP address"))
            .ToList();
        var networks = Values(section, "KnownNetworks")
            .Select(value => IPNetwork.TryParse(value, out var network) ? network : throw Invalid("KnownNetworks", value, "a CIDR range"))
            .ToList();

        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            proxies.ForEach(options.KnownProxies.Add);
            networks.ForEach(options.KnownIPNetworks.Add);
        });
    }

    private static IEnumerable<string> Values(IConfigurationSection section, string key) =>
        (section.GetSection(key).Get<string[]>() ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim());

    private static InvalidOperationException Invalid(string key, string value, string expected) =>
        new($"{SectionName}:{key} contains '{value}', which is not {expected}.");
}
