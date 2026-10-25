using System.Net;
using KcpSharp;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>
/// Один ретранслятор собеседника: его TURN-клиент (наш локальный ретранслятор) и адрес,
/// куда слать пакеты (ретранслятор собеседника). Пары живут парами: наш.Relay ⇄ его.Relay.
/// </summary>
public sealed class RelayPair(TurnClient client, IPEndPoint peerRelay)
{
    public TurnClient Client { get; } = client;
    /// <summary>Адрес ретранслятора собеседника — может меняться при пересопоставлении.</summary>
    public IPEndPoint PeerRelay { get; set; } = peerRelay;
    public long Sent;
    public long Received;
}

/// <summary>
/// Транспорт KCP поверх звонков VK: сегменты KCP шифруются, оборачиваются под RTP и рассыпаются
/// по всем живым парам ретрансляторов (round-robin), поэтому один поток получает суммарную скорость.
/// Принятые пакеты расшифровываются и, если это KCP, скармливаются conversation.
/// </summary>
public sealed class RelayTransport : IKcpTransport, IDisposable
{
    readonly RelayCipher _cipher;
    readonly List<RelayPair> _pairs = [];
    readonly object _lock = new();
    int _roundRobin;

    /// <summary>Пришёл Ping/Pong — для измерения задержки и проверки живости.</summary>
    public event Action<RelayKind, byte[]>? OnControl;

    public RelayTransport(RelayCipher cipher) => _cipher = cipher;

    public IReadOnlyList<RelayPair> Pairs
    {
        get { lock (_lock) return [.. _pairs]; }
    }

    public void AddPair(RelayPair pair)
    {
        HookIncoming(pair);
        lock (_lock) _pairs.Add(pair);
    }

    /// <summary>Подключить приём пакетов пары, не добавляя её в рассылку (для продления).</summary>
    public void HookIncoming(RelayPair pair)
    {
        pair.Client.OnData += (from, data) => OnTurnData(pair, from, data);
    }

    /// <summary>Атомно заменить набор пар (продление) и освободить старые.</summary>
    public void ReplacePairs(IEnumerable<RelayPair> newPairs)
    {
        List<RelayPair> old;
        lock (_lock) { old = [.. _pairs]; _pairs.Clear(); _pairs.AddRange(newPairs); }
        foreach (var p in old) _ = ReleaseAsync(p.Client);
    }

    static async Task ReleaseAsync(TurnClient client)
    {
        try { await client.RefreshAsync(0); } catch { }
        client.Dispose();
    }

    public void RemovePair(RelayPair pair)
    {
        lock (_lock) _pairs.Remove(pair);
        pair.Client.Dispose();
    }

    public long RawReceived;

    void OnTurnData(RelayPair pair, IPEndPoint from, byte[] data)
    {
        Interlocked.Increment(ref RawReceived);
        if (!_cipher.TryOpen(data, out var kind, out var body)) return; // чужой/битый пакет
        Interlocked.Increment(ref pair.Received);
        if (kind == RelayKind.Kcp)
            _ = Conversation?.InputPakcetAsync(body);
        else
            OnControl?.Invoke(kind, body); // Hello/HelloAck/Ping/Pong — управляющий канал
    }

    /// <summary>Назначается после создания conversation, чтобы принятые пакеты попадали в KCP.</summary>
    public KcpConversation? Conversation { get; set; }

    public ValueTask SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken = default)
    {
        SendSealed(RelayKind.Kcp, packet.Span);
        return ValueTask.CompletedTask;
    }

    public void SendControl(RelayKind kind, ReadOnlySpan<byte> body) => SendSealed(kind, body);

    void SendSealed(RelayKind kind, ReadOnlySpan<byte> body)
    {
        var sealed_ = _cipher.Seal(kind, body);
        RelayPair? pair;
        lock (_lock)
        {
            if (_pairs.Count == 0) return;
            pair = _pairs[_roundRobin++ % _pairs.Count];
        }
        pair.Client.Send(pair.PeerRelay, sealed_);
        Interlocked.Increment(ref pair.Sent);
    }

    public void Dispose()
    {
        foreach (var p in Pairs) p.Client.Dispose();
    }
}
