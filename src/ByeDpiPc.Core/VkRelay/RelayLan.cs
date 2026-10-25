using System.Net;
using System.Net.Sockets;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>
/// Прямой UDP-канал по локальной сети — только для связки дома, пока обе стороны не узнали
/// адреса ретрансляторов друг друга. Дальше весь трафик идёт через звонки VK.
/// </summary>
public sealed class RelayLan : IDisposable
{
    readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly CancellationTokenSource _cts = new();

    /// <summary>Пришёл пакет: (отправитель, данные).</summary>
    public event Action<IPEndPoint, byte[]>? OnPacket;

    public int Port { get; }

    public RelayLan(int port = 0)
    {
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _ = ReceiveLoopAsync();
    }

    public void Send(IPEndPoint to, ReadOnlySpan<byte> data)
    {
        try { _socket.SendTo(data, to); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    async Task ReceiveLoopAsync()
    {
        var buffer = new byte[2048];
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await _socket.ReceiveFromAsync(buffer, from, _cts.Token); }
            catch (Exception) { return; }
            OnPacket?.Invoke((IPEndPoint)r.RemoteEndPoint, buffer[..r.ReceivedBytes]);
        }
    }

    /// <summary>Локальные адреса этого ПК (IPv4) для QR-кода связки.</summary>
    public static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    yield return ua.Address;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }
}
