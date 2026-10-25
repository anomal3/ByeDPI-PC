using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ByeDpiPc.Core.VkRelay;

public sealed class TurnException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Мини-клиент TURN (RFC 5766) поверх UDP: Allocate, Refresh, CreatePermission, Send/Data indication.
/// Один экземпляр — один занятый ретранслятор на сервере звонков VK.
/// </summary>
public sealed class TurnClient : IDisposable
{
    const uint Magic = 0x2112A442;
    const ushort AttrUsername = 0x0006, AttrIntegrity = 0x0008, AttrError = 0x0009, AttrLifetime = 0x000D,
        AttrPeer = 0x0012, AttrData = 0x0013, AttrRealm = 0x0014, AttrNonce = 0x0015, AttrRelayed = 0x0016,
        AttrTransport = 0x0019, AttrMapped = 0x0020;

    readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    readonly IPEndPoint _server;
    readonly string _user, _password;
    readonly ConcurrentDictionary<string, TaskCompletionSource<(ushort type, Dictionary<ushort, byte[]> attrs)>> _pending = new();
    readonly CancellationTokenSource _cts = new();
    byte[] _key = [];
    byte[] _realm = [], _nonce = [];

    public IPEndPoint Server => _server;
    /// <summary>Выделенный ретранслятор (relayed transport address) — куда собеседник шлёт пакеты.</summary>
    public IPEndPoint? Relay { get; private set; }
    /// <summary>Наш внешний адрес, как его видит сервер VK.</summary>
    public IPEndPoint? Mapped { get; private set; }
    /// <summary>Пакет от собеседника: (адрес отправителя, тело). Вызывается на фоновом потоке приёма.</summary>
    public event Action<IPEndPoint, byte[]>? OnData;

    public TurnClient(IPEndPoint server, string user, string password)
    {
        _server = server;
        _user = user;
        _password = password;
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        _socket.Connect(server);
        _ = ReceiveLoopAsync();
    }

    public async Task<IPEndPoint> AllocateAsync(CancellationToken ct = default)
    {
        // Первый Allocate без пароля — узнаём realm и nonce.
        var (_, first) = await RequestAsync(0x0003, [(AttrTransport, new byte[] { 17, 0, 0, 0 })], signed: false, ct: ct);
        if (!first.TryGetValue(AttrRealm, out var realm) || !first.TryGetValue(AttrNonce, out var nonce))
            throw new TurnException(0, "сервер TURN не прислал realm/nonce");
        _realm = realm;
        _nonce = nonce;
        _key = MD5.HashData(Encoding.UTF8.GetBytes($"{_user}:{Encoding.UTF8.GetString(_realm)}:{_password}"));

        var (type, attrs) = await RequestAsync(0x0003, [(AttrTransport, new byte[] { 17, 0, 0, 0 })], ct: ct);
        if (type != 0x0103)
            throw new TurnException(ErrorCode(attrs), $"Allocate отказ: {ErrorText(attrs)}");
        Relay = DecodeAddress(attrs[AttrRelayed]);
        if (attrs.TryGetValue(AttrMapped, out var mapped)) Mapped = DecodeAddress(mapped);
        return Relay;
    }

    /// <summary>Продлить выделение (lifetime=0 — освободить ретранслятор).</summary>
    public async Task RefreshAsync(uint lifetime = 600, CancellationToken ct = default)
    {
        var life = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(life, lifetime);
        var (type, attrs) = await RequestAsync(0x0004, [(AttrLifetime, life)], ct: ct);
        if (type == 0x0114 && ErrorCode(attrs) == 438 && attrs.TryGetValue(AttrNonce, out var nonce))
        {
            _nonce = nonce;
            await RequestAsync(0x0004, [(AttrLifetime, life)], ct: ct);
        }
    }

    /// <summary>Разрешить приём от IP (порт игнорируется сервером). Разрешение живёт 5 минут.</summary>
    public async Task<bool> CreatePermissionAsync(IPAddress ip, CancellationToken ct = default)
    {
        var (type, attrs) = await RequestAsync(0x0008, [(AttrPeer, EncodeAddress(new IPEndPoint(ip, 0)))], ct: ct);
        if (type == 0x0118 && ErrorCode(attrs) == 438 && attrs.TryGetValue(AttrNonce, out var nonce))
        {
            _nonce = nonce;
            (type, _) = await RequestAsync(0x0008, [(AttrPeer, EncodeAddress(new IPEndPoint(ip, 0)))], ct: ct);
        }
        return type == 0x0108;
    }

    /// <summary>Отправить данные собеседнику через ретранслятор (Send indication, без подтверждения).</summary>
    public void Send(IPEndPoint peer, ReadOnlySpan<byte> payload)
    {
        var msg = BuildMessage(0x0016, NewTxId(),
            [(AttrPeer, EncodeAddress(peer)), (AttrData, payload.ToArray())], key: null);
        try { _socket.Send(msg); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    async Task<(ushort, Dictionary<ushort, byte[]>)> RequestAsync(ushort type, (ushort, byte[])[] attrs,
        bool signed = true, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var txId = NewTxId();
            var key = Convert.ToHexString(txId);
            var tcs = new TaskCompletionSource<(ushort, Dictionary<ushort, byte[]>)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[key] = tcs;
            var all = signed ? WithAuth(attrs) : attrs;
            var msg = BuildMessage(type, txId, all, signed ? _key : null);
            try { _socket.Send(msg); } catch (Exception) { _pending.TryRemove(key, out _); throw; }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(600 * (attempt + 1)));
            try
            {
                await using (timeout.Token.Register(() => tcs.TrySetCanceled()))
                    return await tcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
            {
                _pending.TryRemove(key, out _);
            }
            finally { _pending.TryRemove(key, out _); }
        }
        throw new TurnException(0, $"сервер TURN {_server} не ответил");
    }

    /// <summary>Отладка: всего пришло датаграмм / из них Data indication.</summary>
    public long TotalDatagrams, DataIndications;

    async Task ReceiveLoopAsync()
    {
        var buffer = new byte[2048];
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try { n = await _socket.ReceiveAsync(buffer, _cts.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref TotalDatagrams);
            if (n < 20) continue;
            if (!TryParse(buffer.AsSpan(0, n), out var type, out var txId, out var attrs)) continue;

            if (type == 0x0017) // Data indication от собеседника
            {
                Interlocked.Increment(ref DataIndications);
                if (attrs.TryGetValue(AttrPeer, out var peer) && attrs.TryGetValue(AttrData, out var data))
                    OnData?.Invoke(DecodeAddress(peer), data);
            }
            else if (_pending.TryRemove(Convert.ToHexString(txId), out var tcs))
            {
                tcs.TrySetResult((type, attrs));
            }
        }
    }

    (ushort, byte[])[] WithAuth((ushort, byte[])[] attrs)
    {
        var list = new List<(ushort, byte[])>
        {
            (AttrUsername, Encoding.UTF8.GetBytes(_user)),
            (AttrRealm, _realm),
            (AttrNonce, _nonce),
        };
        list.AddRange(attrs);
        return [.. list];
    }

    static byte[] NewTxId() => RandomNumberGenerator.GetBytes(12);

    static byte[] BuildMessage(ushort type, byte[] txId, (ushort t, byte[] v)[] attrs, byte[]? key)
    {
        using var body = new MemoryStream();
        foreach (var (t, v) in attrs)
        {
            Span<byte> head = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(head, t);
            BinaryPrimitives.WriteUInt16BigEndian(head[2..], (ushort)v.Length);
            body.Write(head);
            body.Write(v);
            for (var pad = (4 - v.Length % 4) % 4; pad > 0; pad--) body.WriteByte(0);
        }

        if (key is not null)
        {
            // MESSAGE-INTEGRITY считается по заголовку с длиной, уже включающей сам атрибут (24 байта).
            var prefix = Header(type, (int)body.Length + 24, txId);
            var mac = new HMACSHA1(key).ComputeHash(Concat(prefix, body.ToArray()));
            Span<byte> head = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(head, AttrIntegrity);
            BinaryPrimitives.WriteUInt16BigEndian(head[2..], 20);
            body.Write(head);
            body.Write(mac);
        }

        return Concat(Header(type, (int)body.Length, txId), body.ToArray());
    }

    static byte[] Header(ushort type, int length, byte[] txId)
    {
        var head = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(head, type);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), (ushort)length);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), Magic);
        txId.CopyTo(head.AsSpan(8));
        return head;
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    static bool TryParse(ReadOnlySpan<byte> data, out ushort type, out byte[] txId, out Dictionary<ushort, byte[]> attrs)
    {
        type = 0; txId = []; attrs = [];
        if (data.Length < 20 || data[0] >= 0x40) return false;
        type = BinaryPrimitives.ReadUInt16BigEndian(data);
        int length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (data.Length < 20 + length) return false;
        txId = data.Slice(8, 12).ToArray();
        attrs = new Dictionary<ushort, byte[]>();
        var i = 20;
        while (i + 4 <= 20 + length)
        {
            var t = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            int l = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (i + 4 + l > data.Length) break;
            attrs.TryAdd(t, data.Slice(i + 4, l).ToArray());
            i += 4 + l + (4 - l % 4) % 4;
        }
        return true;
    }

    static IPEndPoint DecodeAddress(byte[] v)
    {
        // XOR-MAPPED-ADDRESS: порт и адрес зашифрованы по Magic.
        var port = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(2)) ^ (Magic >> 16));
        Span<byte> ip = stackalloc byte[4];
        var magic = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(magic, Magic);
        for (var k = 0; k < 4; k++) ip[k] = (byte)(v[4 + k] ^ magic[k]);
        return new IPEndPoint(new IPAddress(ip), port);
    }

    static byte[] EncodeAddress(IPEndPoint ep)
    {
        var magic = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(magic, Magic);
        var v = new byte[8];
        v[0] = 0;
        v[1] = 1; // IPv4
        BinaryPrimitives.WriteUInt16BigEndian(v.AsSpan(2), (ushort)(ep.Port ^ (Magic >> 16)));
        var ip = ep.Address.GetAddressBytes();
        for (var k = 0; k < 4; k++) v[4 + k] = (byte)(ip[k] ^ magic[k]);
        return v;
    }

    static int ErrorCode(Dictionary<ushort, byte[]> attrs)
        => attrs.TryGetValue(AttrError, out var e) && e.Length >= 4 ? e[2] * 100 + e[3] : 0;

    static string ErrorText(Dictionary<ushort, byte[]> attrs)
        => attrs.TryGetValue(AttrError, out var e) && e.Length >= 4
            ? $"{e[2] * 100 + e[3]} {Encoding.UTF8.GetString(e, 4, e.Length - 4)}"
            : "без кода";

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _cts.Dispose();
    }
}
