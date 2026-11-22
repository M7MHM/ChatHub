using System.Net;
using System.Net.Sockets;

namespace ChatHub.Notifications.Tests.Infrastructure;

public sealed class CountingTcpProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly List<TcpClient> open = [];
    private readonly string targetHost;
    private readonly int targetPort;
    private readonly Task acceptLoop;
    private int accepted;

    public CountingTcpProxy(string targetHost, int targetPort)
    {
        this.targetHost = targetHost;
        this.targetPort = targetPort;
        listener.Start();
        acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int AcceptedConnections => Volatile.Read(ref accepted);

    public void DropAll()
    {
        lock (open)
        {
            foreach (var connection in open) connection.Close();
            open.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        DropAll();
        try
        {
            await acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception) when (stopping.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Increment(ref accepted);
            var outbound = new TcpClient();
            await outbound.ConnectAsync(targetHost, targetPort, stopping.Token);
            lock (open) open.AddRange([inbound, outbound]);
            _ = PumpAsync(inbound, outbound);
            _ = PumpAsync(outbound, inbound);
        }
    }

    private static async Task PumpAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception)
        {
        }
        finally
        {
            from.Close();
            to.Close();
        }
    }
}
