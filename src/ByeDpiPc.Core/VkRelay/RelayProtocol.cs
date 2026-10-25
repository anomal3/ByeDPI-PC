using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>Тип открытого сообщения (первый байт расшифрованного тела).</summary>
public enum RelayKind : byte
{
    Hello = 1,
    HelloAck = 2,
    Kcp = 3,
    Ping = 4,
    Pong = 5,
}

/// <summary>Роль стороны в связке.</summary>
public enum RelayRole { Pc, Phone }

/// <summary>Приветствие при связке и переподключении (см. docs/vk-relay.md).</summary>
public sealed class RelayHello
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    /// <summary>Адреса наших ретрансляторов (ip:port), куда собеседник должен слать пакеты.</summary>
    [JsonPropertyName("relays")] public List<string> Relays { get; set; } = [];
    /// <summary>Данные TURN (кладёт только телефон); ПК берёт их отсюда.</summary>
    [JsonPropertyName("creds")] public RelayCreds? Creds { get; set; }
    /// <summary>Идентификатор сеанса KCP (conv).</summary>
    [JsonPropertyName("session")] public uint Session { get; set; }
}

public sealed class RelayCreds
{
    [JsonPropertyName("u")] public string Username { get; set; } = "";
    [JsonPropertyName("p")] public string Password { get; set; } = "";
    [JsonPropertyName("turn")] public List<string> Turn { get; set; } = [];
}

/// <summary>
/// Шифрование и обёртка пакетов под медиапоток звонка. Пакет на проводе:
/// RTP-заголовок 12 байт (как AAD) + счётчик 8 байт + ChaCha20-Poly1305(тело)+тег 16.
/// </summary>
public sealed class RelayCipher
{
    readonly byte[] _sendKey, _recvKey;
    readonly uint _ssrc;
    long _counter;

    public RelayCipher(byte[] secret, RelayRole role)
    {
        // Ключи направлений расходятся по info, чтобы отправка и приём не пересекались.
        var pcToPhone = Hkdf(secret, "pc->phone");
        var phoneToPc = Hkdf(secret, "phone->pc");
        (_sendKey, _recvKey) = role == RelayRole.Pc ? (pcToPhone, phoneToPc) : (phoneToPc, pcToPhone);
        _ssrc = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    }

    public const int MaxPacket = 1200;
    public const int Overhead = 12 + 8 + 16;
    public const int MaxPayload = MaxPacket - Overhead;

    static byte[] Hkdf(byte[] secret, string info)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32,
            salt: Encoding.ASCII.GetBytes("byedpi-vk-relay"), info: Encoding.ASCII.GetBytes(info));

    public byte[] Seal(RelayKind kind, ReadOnlySpan<byte> body)
    {
        var plain = new byte[1 + body.Length];
        plain[0] = (byte)kind;
        body.CopyTo(plain.AsSpan(1));

        var counter = (ulong)Interlocked.Increment(ref _counter);
        var packet = new byte[12 + 8 + plain.Length + 16];
        WriteRtpHeader(packet, counter);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(12), counter);

        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(nonce, _ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);

        using var chacha = new ChaCha20Poly1305(_sendKey);
        chacha.Encrypt(nonce, plain, packet.AsSpan(20, plain.Length), packet.AsSpan(20 + plain.Length, 16),
            associatedData: packet.AsSpan(0, 12));
        return packet;
    }

    public bool TryOpen(ReadOnlySpan<byte> packet, out RelayKind kind, out byte[] body)
    {
        kind = default; body = [];
        if (packet.Length < 12 + 8 + 16 + 1) return false;
        var counter = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(12, 8));

        // ssrc отправителя записан в заголовке; у каждой стороны он свой.
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4));
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(nonce, ssrc);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);

        var cipherLen = packet.Length - 20 - 16;
        var plain = new byte[cipherLen];
        try
        {
            using var chacha = new ChaCha20Poly1305(_recvKey);
            chacha.Decrypt(nonce, packet.Slice(20, cipherLen), packet.Slice(20 + cipherLen, 16), plain,
                associatedData: packet.Slice(0, 12));
        }
        catch (AuthenticationTagMismatchException) { return false; }
        if (plain.Length < 1) return false;
        kind = (RelayKind)plain[0];
        body = plain[1..];
        return true;
    }

    void WriteRtpHeader(Span<byte> p, ulong counter)
    {
        p[0] = 0x80;              // версия 2, без расширений
        p[1] = 0x6F;              // payload type 111 (как opus в звонках)
        BinaryPrimitives.WriteUInt16BigEndian(p[2..], (ushort)(counter & 0xFFFF));
        BinaryPrimitives.WriteUInt32BigEndian(p[4..], (uint)(counter * 960));
        BinaryPrimitives.WriteUInt32BigEndian(p[8..], _ssrc);
    }
}
