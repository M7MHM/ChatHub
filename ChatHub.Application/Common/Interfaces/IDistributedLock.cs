namespace ChatHub.Application.Common.Interfaces;

public interface IDistributedLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken cancellationToken);
}
