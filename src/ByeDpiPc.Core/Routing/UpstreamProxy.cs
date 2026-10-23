using System.Text.Json.Nodes;
using System.Web;

namespace ByeDpiPc.Core.Routing;

public enum UpstreamKind { Socks5, Http, Https, Vless, Ssh }

/// <summary>Параметры VLESS (Reality / TLS) из ссылки vless://.</summary>
public sealed record VlessParams(
    string Uuid, string Security, string? Sni, string? PublicKey, string? ShortId, string Fingerprint,
    string? Flow, string Transport, string? Path, string? HostHeader, string? ServiceName);

/// <summary>
/// Внешний прокси пользователя для сервисов, заблокированных по IP:
/// socks5://, http(s)://, vless:// (Reality/TLS) или ssh://user:pass@host:port (SSH-туннель; VLESS и SSH работают через sing-box).
/// </summary>
public sealed record UpstreamProxy(UpstreamKind Kind, string Host, int Port, string? User, string? Password)
{
    public VlessParams? Vless { get; init; }
    /// <summary>Подпись из ссылки (#имя).</summary>
    public string? Label { get; init; }

    public static bool TryParse(string text, out UpstreamProxy? proxy, out string error)
    {
        proxy = null;
        error = "";
        text = text.Trim();
        if (text.Length == 0)
        {
            error = "адрес не указан";
            return false;
        }
        if (text.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return TryParseVless(text, out proxy, out error);
        string? label = null;
        if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) && text.IndexOf('#') is var h and >= 0)
        {
            label = Uri.UnescapeDataString(text[(h + 1)..]);
            text = text[..h];
        }
        if (!text.Contains("://")) text = "socks5://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "не похоже на адрес прокси. Пример: socks5://user:pass@1.2.3.4:1080 или ссылка vless://…";
            return false;
        }
        UpstreamKind kind;
        switch (uri.Scheme.ToLowerInvariant())
        {
            case "socks" or "socks5" or "socks5h": kind = UpstreamKind.Socks5; break;
            case "http": kind = UpstreamKind.Http; break;
            case "https": kind = UpstreamKind.Https; break;
            case "ssh": kind = UpstreamKind.Ssh; break;
            default:
                error = $"протокол «{uri.Scheme}» не поддерживается. Поддерживаются vless://, ssh://, socks5://, http://, https://";
                return false;
        }
        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "не указан адрес сервера";
            return false;
        }
        var port = uri.IsDefaultPort || uri.Port <= 0
            ? kind switch { UpstreamKind.Socks5 => 1080, UpstreamKind.Https => 443, UpstreamKind.Ssh => 22, _ => 8080 }
            : uri.Port;
        string? user = null, pass = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var ui = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(ui[0]);
            pass = ui.Length > 1 ? Uri.UnescapeDataString(ui[1]) : null;
        }
        if (kind == UpstreamKind.Ssh && (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)))
        {
            error = "в ссылке ssh:// нужны имя пользователя и пароль: ssh://user:pass@host:port";
            return false;
        }
        proxy = new UpstreamProxy(kind, uri.Host.Trim('[', ']'), port, user, pass) { Label = label };
        return true;
    }

    /// <summary>vless://uuid@host:port?security=reality&amp;pbk=…&amp;sid=…&amp;sni=…&amp;fp=chrome&amp;flow=xtls-rprx-vision&amp;type=tcp#имя</summary>
    private static bool TryParseVless(string text, out UpstreamProxy? proxy, out string error)
    {
        proxy = null;
        error = "";
        var hash = text.IndexOf('#');
        string? label = hash >= 0 ? Uri.UnescapeDataString(text[(hash + 1)..]) : null;
        if (hash >= 0) text = text[..hash];
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = "ссылка vless:// повреждена — скопируйте её целиком";
            return false;
        }
        var uuid = Uri.UnescapeDataString(uri.UserInfo);
        if (!Guid.TryParse(uuid, out _))
        {
            error = "в ссылке vless:// нет корректного ключа (UUID)";
            return false;
        }
        var q = HttpUtility.ParseQueryString(uri.Query);
        var security = (q["security"] ?? "none").ToLowerInvariant();
        if (security is not ("reality" or "tls" or "none"))
        {
            error = $"шифрование «{security}» не поддерживается (нужно reality или tls)";
            return false;
        }
        if (security == "reality" && string.IsNullOrEmpty(q["pbk"]))
        {
            error = "в ссылке Reality нет открытого ключа (pbk)";
            return false;
        }
        var transport = (q["type"] ?? "tcp").ToLowerInvariant();
        if (transport is not ("tcp" or "grpc" or "ws" or "httpupgrade"))
        {
            error = $"транспорт «{transport}» не поддерживается (поддерживаются tcp, grpc, ws, httpupgrade)";
            return false;
        }
        var vless = new VlessParams(
            uuid, security, NullIfEmpty(q["sni"]) ?? (security == "none" ? null : uri.Host), NullIfEmpty(q["pbk"]), NullIfEmpty(q["sid"]),
            NullIfEmpty(q["fp"]) ?? "chrome", NullIfEmpty(q["flow"]), transport, NullIfEmpty(q["path"]),
            NullIfEmpty(q["host"]), NullIfEmpty(q["serviceName"]));
        proxy = new UpstreamProxy(UpstreamKind.Vless, uri.Host.Trim('[', ']'), uri.IsDefaultPort || uri.Port <= 0 ? 443 : uri.Port, null, null)
        {
            Vless = vless,
            Label = label,
        };
        return true;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>VLESS нельзя отдать браузеру через PAC напрямую — для режима «Только прокси» нужен локальный sing-box.</summary>
    public bool NeedsLocalBridge => Kind is UpstreamKind.Vless or UpstreamKind.Ssh;

    /// <summary>Запись для PAC-файла (авторизацию PAC не поддерживает).</summary>
    public string PacEntry => Kind == UpstreamKind.Socks5
        ? $"SOCKS5 {HostPort}; SOCKS {HostPort}"
        : Kind == UpstreamKind.Https ? $"HTTPS {HostPort}" : $"PROXY {HostPort}";

    public string HostPort => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>Короткое описание для интерфейса (без секретов).</summary>
    public string Describe() => Kind switch
    {
        UpstreamKind.Vless => $"VLESS{(Vless!.Security == "reality" ? " Reality" : Vless.Security == "tls" ? " TLS" : "")} · {HostPort}" + (Label != null ? $" · {Label}" : ""),
        UpstreamKind.Socks5 => $"SOCKS5 · {HostPort}",
        UpstreamKind.Ssh => $"SSH-туннель · {User}@{HostPort}" + (Label != null ? $" · {Label}" : ""),
        _ => $"{Kind.ToString().ToUpperInvariant()} · {HostPort}",
    };

    /// <summary>Адрес для HttpClient (проверка в настройках; для VLESS не подходит).</summary>
    public Uri ToUri()
    {
        var scheme = Kind switch { UpstreamKind.Socks5 => "socks5", UpstreamKind.Https => "https", _ => "http" };
        return new Uri($"{scheme}://{HostPort}");
    }

    /// <summary>Выход sing-box с тегом <paramref name="tag"/>.</summary>
    public JsonObject ToSingBoxOutbound(string tag = "upstream")
    {
        if (Kind == UpstreamKind.Vless)
        {
            var v = Vless!;
            var o = new JsonObject
            {
                ["type"] = "vless", ["tag"] = tag, ["server"] = Host, ["server_port"] = Port, ["uuid"] = v.Uuid,
            };
            if (v.Flow != null && v.Transport == "tcp") o["flow"] = v.Flow;
            if (v.Security != "none")
            {
                var tls = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = v.Sni ?? Host,
                    ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = v.Fingerprint },
                };
                if (v.Security == "reality")
                {
                    var reality = new JsonObject { ["enabled"] = true, ["public_key"] = v.PublicKey };
                    if (v.ShortId != null) reality["short_id"] = v.ShortId;
                    tls["reality"] = reality;
                }
                o["tls"] = tls;
            }
            switch (v.Transport)
            {
                case "grpc":
                    o["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = v.ServiceName ?? "" };
                    break;
                case "ws" or "httpupgrade":
                    var t = new JsonObject { ["type"] = v.Transport, ["path"] = v.Path ?? "/" };
                    if (v.HostHeader != null)
                    {
                        if (v.Transport == "ws") t["headers"] = new JsonObject { ["Host"] = v.HostHeader };
                        else t["host"] = v.HostHeader;
                    }
                    o["transport"] = t;
                    break;
            }
            o["packet_encoding"] = "xudp";
            return o;
        }

        if (Kind == UpstreamKind.Ssh)
        {
            return new JsonObject
            {
                ["type"] = "ssh", ["tag"] = tag, ["server"] = Host, ["server_port"] = Port,
                ["user"] = User, ["password"] = Password,
            };
        }

        var p = new JsonObject
        {
            ["type"] = Kind == UpstreamKind.Socks5 ? "socks" : "http",
            ["tag"] = tag,
            ["server"] = Host,
            ["server_port"] = Port,
        };
        if (Kind == UpstreamKind.Socks5) p["version"] = "5";
        if (User != null) p["username"] = User;
        if (Password != null) p["password"] = Password;
        if (Kind == UpstreamKind.Https) p["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = Host };
        return p;
    }
}
