namespace ByeDpiPc.Core.Routing;

public enum UpstreamKind { Socks5, Http, Https }

/// <summary>Внешний прокси пользователя для сервисов, заблокированных по IP.</summary>
public sealed record UpstreamProxy(UpstreamKind Kind, string Host, int Port, string? User, string? Password)
{
    public static bool TryParse(string text, out UpstreamProxy? proxy, out string error)
    {
        proxy = null;
        error = "";
        text = text.Trim();
        if (!text.Contains("://")) text = "socks5://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "не похоже на адрес прокси. Пример: socks5://user:pass@1.2.3.4:1080";
            return false;
        }
        UpstreamKind kind;
        switch (uri.Scheme.ToLowerInvariant())
        {
            case "socks" or "socks5" or "socks5h": kind = UpstreamKind.Socks5; break;
            case "http": kind = UpstreamKind.Http; break;
            case "https": kind = UpstreamKind.Https; break;
            default:
                error = $"протокол «{uri.Scheme}» не поддерживается. Поддерживаются socks5://, http://, https://";
                return false;
        }
        if (string.IsNullOrEmpty(uri.Host))
        {
            error = "не указан адрес сервера";
            return false;
        }
        var port = uri.IsDefaultPort || uri.Port <= 0
            ? kind switch { UpstreamKind.Socks5 => 1080, UpstreamKind.Https => 443, _ => 8080 }
            : uri.Port;
        string? user = null, pass = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var ui = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(ui[0]);
            pass = ui.Length > 1 ? Uri.UnescapeDataString(ui[1]) : null;
        }
        proxy = new UpstreamProxy(kind, uri.Host.Trim('[', ']'), port, user, pass);
        return true;
    }

    /// <summary>Запись для PAC-файла (авторизацию PAC не поддерживает).</summary>
    public string PacEntry => Kind == UpstreamKind.Socks5
        ? $"SOCKS5 {HostPort}; SOCKS {HostPort}"
        : Kind == UpstreamKind.Https ? $"HTTPS {HostPort}" : $"PROXY {HostPort}";

    public string HostPort => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>Адрес для HttpClient (проверка в настройках).</summary>
    public Uri ToUri()
    {
        var scheme = Kind switch { UpstreamKind.Socks5 => "socks5", UpstreamKind.Https => "https", _ => "http" };
        return new Uri($"{scheme}://{HostPort}");
    }
}
