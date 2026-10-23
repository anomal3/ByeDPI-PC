using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ByeDpiPc.Core.Engine;

/// <summary>Мини-HTTP-сервер на 127.0.0.1, который отдаёт PAC-файл (без прав администратора).</summary>
public sealed class PacServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private volatile byte[] _body;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/proxy.pac";

    public PacServer(string pac)
    {
        _body = Encoding.UTF8.GetBytes(pac);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public void Update(string pac) => _body = Encoding.UTF8.GetBytes(pac);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buf = new byte[4096];
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(3000);
                // Достаточно прочитать начало запроса — отвечаем PAC на любой путь.
                await stream.ReadAsync(buf, cts.Token);
                var body = _body;
                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\n" +
                    $"Content-Length: {body.Length}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cts.Token);
                await stream.WriteAsync(body, cts.Token);
            }
            catch { /* клиент отвалился */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
