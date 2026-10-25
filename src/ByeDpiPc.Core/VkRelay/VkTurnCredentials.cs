using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ByeDpiPc.Core.VkRelay;

/// <summary>Данные доступа к ретрансляторам VK, полученные по ссылке на звонок.</summary>
public sealed class TurnCredentials
{
    public required string Username { get; init; }
    public required string Password { get; init; }
    /// <summary>Адреса TURN-серверов (ip:порт, только UDP).</summary>
    public required List<IPEndPoint> Servers { get; init; }
    /// <summary>Когда доступ перестаёт действовать (из username = "unix:id").</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public bool Expired => DateTimeOffset.UtcNow > ExpiresAt - TimeSpan.FromMinutes(5);
}

/// <summary>Телефону VK показал капчу: нужно решить «Я не робот» и повторить.</summary>
public sealed class VkCaptchaRequired(string redirectUri) : Exception("VK требует капчу")
{
    public string RedirectUri { get; } = redirectUri;
}

/// <summary>
/// Получение данных TURN по ссылке на звонок VK — анонимная цепочка (как в vk-turn-proxy).
/// На ПК обычно не используется (данные присылает телефон); оставлено для диагностики и запаса.
/// </summary>
public sealed class VkTurnCredentials
{
    static readonly (string Id, string Secret)[] Apps =
    [
        ("6287487", "QbYic1K3lEV5kTGiqlq2"),
        ("7879029", "aR5NKGmm03GYrCiNKsaw"),
        ("52461373", "o557NLIkAErNhakXrQ7A"),
        ("52649896", "WStp4ihWG4l3nmXZgIbC"),
        ("51781872", "IjjCNl4L4Tf5QZEXIHKK"),
    ];
    const string OkAppKey = "CGMMEJLGDIHBABABA";
    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

    readonly HttpClient _http;

    public VkTurnCredentials(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    /// <summary>
    /// Пройти цепочку и получить данные TURN. <paramref name="successToken"/> — результат решённой
    /// капчи (при повторе); при первом вызове null.
    /// </summary>
    public async Task<TurnCredentials> FetchAsync(string callLink, string? successToken = null,
        string? captchaSid = null, CancellationToken ct = default)
    {
        var hash = ExtractHash(callLink);
        var join = $"https://vk.com/call/join/{hash}";
        Exception? last = null;

        foreach (var (id, secret) in Apps)
        {
            try
            {
                var token1 = await GetAnonymTokenAsync(id, secret, ct);
                await Post("https://api.vk.ru/method/calls.getCallPreview?v=5.275&client_id=" + id,
                    new() { ["vk_join_link"] = join, ["fields"] = "photo_200", ["access_token"] = token1 }, ct);

                var fields = new Dictionary<string, string>
                {
                    ["vk_join_link"] = join, ["name"] = "Гость", ["access_token"] = token1,
                };
                if (successToken is not null)
                {
                    fields["captcha_key"] = "";
                    fields["captcha_sid"] = captchaSid ?? "";
                    fields["is_sound_captcha"] = "0";
                    fields["success_token"] = successToken;
                }
                var token2Resp = await Post(
                    "https://api.vk.ru/method/calls.getAnonymousToken?v=5.275&client_id=" + id, fields, ct);
                if (token2Resp.TryGetProperty("error", out var err))
                {
                    if (err.TryGetProperty("error_code", out var code) && code.GetInt32() == 14 &&
                        err.TryGetProperty("redirect_uri", out var uri))
                        throw new VkCaptchaRequired(uri.GetString() ?? "");
                    throw new TurnException(0, "getAnonymousToken: " + err);
                }
                var token2 = token2Resp.GetProperty("response").GetProperty("token").GetString()!;

                var sessionData = JsonSerializer.Serialize(new
                {
                    version = 2,
                    device_id = Guid.NewGuid().ToString(),
                    client_version = 1.1,
                    client_type = "SDK_JS",
                });
                var sessionResp = await Post("https://calls.okcdn.ru/fb.do", new()
                {
                    ["session_data"] = sessionData, ["method"] = "auth.anonymLogin",
                    ["format"] = "JSON", ["application_key"] = OkAppKey,
                }, ct);
                var sessionKey = sessionResp.GetProperty("session_key").GetString()!;

                var join2 = await Post("https://calls.okcdn.ru/fb.do", new()
                {
                    ["joinLink"] = hash, ["isVideo"] = "false", ["protocolVersion"] = "5",
                    ["capabilities"] = "2F7F", ["anonymToken"] = token2,
                    ["method"] = "vchat.joinConversationByLink", ["format"] = "JSON",
                    ["application_key"] = OkAppKey, ["session_key"] = sessionKey,
                }, ct);
                return Parse(join2);
            }
            catch (VkCaptchaRequired) { throw; }
            catch (Exception e) { last = e; }
        }
        throw new TurnException(0, "не удалось получить данные TURN: " + last?.Message);
    }

    async Task<string> GetAnonymTokenAsync(string id, string secret, CancellationToken ct)
    {
        var resp = await Post("https://login.vk.ru/?act=get_anonym_token", new()
        {
            ["client_id"] = id, ["token_type"] = "messages", ["client_secret"] = secret,
            ["version"] = "1", ["app_id"] = id,
        }, ct);
        return resp.GetProperty("data").GetProperty("access_token").GetString()!;
    }

    async Task<JsonElement> Post(string url, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        req.Headers.TryAddWithoutValidation("Origin", "https://vk.ru");
        req.Headers.TryAddWithoutValidation("Referer", "https://vk.ru/");
        using var resp = await _http.SendAsync(req, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc;
    }

    static TurnCredentials Parse(JsonElement join)
    {
        if (!join.TryGetProperty("turn_server", out var ts))
            throw new TurnException(0, "в ответе нет turn_server: " + join);
        var user = ts.GetProperty("username").GetString()!;
        var pass = ts.GetProperty("credential").GetString()!;
        var servers = new List<IPEndPoint>();
        foreach (var u in ts.GetProperty("urls").EnumerateArray())
        {
            var s = u.GetString();
            if (s is null || s.Contains("transport=tcp")) continue;
            var clean = s.Split('?')[0];
            var addr = clean.StartsWith("turns:") ? clean[6..] : clean.StartsWith("turn:") ? clean[5..] : clean;
            if (IPEndPoint.TryParse(addr, out var ep)) servers.Add(ep);
        }
        var expiry = long.TryParse(user.Split(':')[0], out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.UtcNow.AddHours(8);
        return new TurnCredentials { Username = user, Password = pass, Servers = servers, ExpiresAt = expiry };
    }

    static string ExtractHash(string link)
        => link.TrimEnd('/').Split('/')[^1].Split('?')[0];
}
