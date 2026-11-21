using Testcontainers.Redis;

namespace ChatHub.Api.IntegrationTests.Infrastructure;

public class RedisApiFixture : ApiFixture
{
    public RedisContainer Redis { get; } = new RedisBuilder("redis:8-alpine").Build();

    protected override string? RedisConnectionString => $"{Redis.GetConnectionString()},abortConnect=false";

    public override async Task InitializeAsync()
    {
        await Redis.StartAsync();
        await base.InitializeAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await Redis.DisposeAsync();
    }

    public async Task<IReadOnlyList<string>> RedisKeysAsync(string pattern)
    {
        var result = await Redis.ExecAsync(["redis-cli", "--scan", "--pattern", pattern]);
        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

[CollectionDefinition(Name)]
public sealed class RedisApiCollection : ICollectionFixture<RedisApiFixture>
{
    public const string Name = "Api with Redis";
}

[CollectionDefinition(Name)]
public sealed class RedisOutageCollection : ICollectionFixture<RedisApiFixture>
{
    public const string Name = "Api with a Redis outage";
}
