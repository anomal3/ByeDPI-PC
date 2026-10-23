using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ByeDpiPc.Core.Engine;

public static class NetUtil
{
    public static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static bool IsPortBusy(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    /// <summary>Ждёт, пока на 127.0.0.1:port начнут принимать подключения.</summary>
    public static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, Func<bool>? abort = null, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (abort?.Invoke() == true) return false;
            try
            {
                using var c = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(300);
                await c.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                await Task.Delay(80, ct);
            }
        }
        return false;
    }
}
