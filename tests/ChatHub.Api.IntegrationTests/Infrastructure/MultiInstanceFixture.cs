using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public sealed class MultiInstanceFixture : RedisApiFixture
{
    private ChatHubApiFactory? _second;

    public ChatHubApiFactory Second => _second ?? throw new InvalidOperationException("The second host is not started.");

    public TestServer SecondServer => Second.Server;

    protected override IReadOnlyDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>
    {
        ["RealTime:HeartbeatIntervalSeconds"] = "1",
        ["RealTime:InstanceTimeoutSeconds"] = "10",
        ["Diagnostics:ExposeInstanceId"] = "true"
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _second = CreateFactory();
        _ = _second.Server;
    }

    public override async Task ResetDatabaseAsync()
    {
        await base.ResetDatabaseAsync();
        await Second.Services.GetRequiredService<HybridCache>().RemoveByTagAsync("*");
    }

    public override async Task DisposeAsync()
    {
        if (_second is not null) await _second.DisposeAsync();
        await base.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class MultiInstanceCollection : ICollectionFixture<MultiInstanceFixture>
{
    public const string Name = "Api on two instances sharing Redis";
}
