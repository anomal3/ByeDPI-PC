using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>
/// Содержимое QR-кода связки: секрет шифрования и адреса ПК в локальной сети, куда телефон шлёт
/// приветствие. Создаёт ПК, читает телефон. Секрет одноразовый на связку.
/// </summary>
public sealed class RelayPairing
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    /// <summary>32 байта секрета в base64url.</summary>
    [JsonPropertyName("s")] public string Secret { get; set; } = "";
    /// <summary>Адреса ПК в локальной сети (ip:port).</summary>
    [JsonPropertyName("lan")] public List<string> Lan { get; set; } = [];
    /// <summary>Ссылка на звонок VK — чтобы телефон не вводил её вручную, а взял из QR.</summary>
    [JsonPropertyName("link")] public string CallLink { get; set; } = "";

    public byte[] SecretBytes => Base64Url.Decode(Secret);

    public static RelayPairing Create(IEnumerable<string> lan) => new()
    {
        Secret = Base64Url.Encode(RandomNumberGenerator.GetBytes(32)),
        Lan = lan.ToList(),
    };

    /// <summary>Строка для QR-кода: «bdpi-vk:» + base64url(JSON).</summary>
    public string ToCode()
        => "bdpi-vk:" + Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(this));

    public static RelayPairing? Parse(string code)
    {
        const string prefix = "bdpi-vk:";
        if (!code.StartsWith(prefix)) return null;
        try
        {
            var json = Base64Url.Decode(code[prefix.Length..]);
            var p = JsonSerializer.Deserialize<RelayPairing>(json);
            return p is { Version: 1 } && p.Secret.Length > 0 ? p : null;
        }
        catch { return null; }
    }
}

static class Base64Url
{
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }
}
