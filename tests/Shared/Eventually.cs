using System.Diagnostics;

namespace ChatHub.Tests;

public static class Eventually
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public static async Task<bool> TryAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (await condition()) return true;
            if (elapsed.Elapsed >= limit) return false;
            await Task.Delay(PollInterval);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string failure, TimeSpan? timeout = null)
    {
        if (!await TryAsync(condition, timeout))
            throw new ShouldAssertException($"{failure} (waited {(timeout ?? Timeout).TotalSeconds:0} s)");
    }

    public static Task UntilAsync(Func<bool> condition, string failure, TimeSpan? timeout = null) =>
        UntilAsync(() => Task.FromResult(condition()), failure, timeout);
}
