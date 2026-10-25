using System.Net.Sockets;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>
/// Сторона ПК: на каждый запрос OPEN от телефона подключается к целевому host:port и перекачивает
/// данные между TCP-соединением и потоком мультиплексора в обе стороны.
/// </summary>
public sealed class RelayPcService
{
    readonly RelayEndpoint _endpoint;

    public event Action<string>? Log;

    public RelayPcService(RelayEndpoint endpoint)
    {
        _endpoint = endpoint;
        _endpoint.OnOpen = OpenAsync;
    }

    async Task OpenAsync(string host, int port, RelayStream stream)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(15));
        tcp.NoDelay = true;
        Log?.Invoke($"→ {host}:{port}");
        _ = PumpAsync(tcp, stream);
    }

    static async Task PumpAsync(TcpClient tcp, RelayStream stream)
    {
        using var _ = tcp;
        var net = tcp.GetStream();
        var up = Task.Run(async () =>
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (true)
                {
                    var n = await net.ReadAsync(buffer);
                    if (n == 0) break;
                    await stream.SendAsync(buffer.AsMemory(0, n));
                }
            }
            catch { }
        });
        var down = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var data = await stream.ReceiveAsync();
                    if (data is null) break;
                    await net.WriteAsync(data);
                }
            }
            catch { }
        });
        await Task.WhenAny(up, down);
        try { tcp.Close(); } catch { }
        await stream.CloseAsync();
    }
}
