namespace ChatHub.Infrastructure.RealTime;

public sealed record ServerInstance(string Id)
{
    public static ServerInstance Create() => new(Guid.NewGuid().ToString("N"));
}
