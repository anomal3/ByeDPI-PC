using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ByeDpiPc.Core.Testing;

/// <summary>
/// Проверка сайта системным curl (Windows 10+, macOS, Linux).
/// DPI провайдера смотрит на отпечаток TLS ClientHello. Клиент .NET (SslStream) выглядит иначе, чем браузер,
/// и через ту же стратегию обхода не проходит, хотя Chrome и системный curl открывают сайт. Поэтому
/// проверять надо клиентом, похожим на браузер: так подбор видит ровно то, что увидит пользователь.
/// Для видеоузлов YouTube (rrN---sn-….googlevideo.com) провайдеры блокируют отдельно от сайта: сайт и картинки
/// открываются, а видео нет. Такой узел проверяется через --resolve на IP redirector.googlevideo.com.
/// </summary>
public static class CurlProbe
{
    private static readonly string CurlPath = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "curl.exe") : "/usr/bin/curl";
    private static readonly string NullDevice = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    /// <summary>На Windows curl.exe (Schannel) проверяет отзыв сертификата, и эта проверка сама упирается в блокировки — отключаем.</summary>
    private static readonly string[] PlatformArgs = OperatingSystem.IsWindows() ? new[] { "--ssl-no-revoke" } : Array.Empty<string>();
    private const string VideoAddressHost = "redirector.googlevideo.com";

    /// <summary>Имя видеоузла для списка сайтов: живой узел не нужен, DPI смотрит только на SNI.</summary>
    public const string VideoNodeHost = "rr1---sn-5hne6nzk.googlevideo.com";

    /// <summary>
    /// Сайты, без которых YouTube не работает: страница, API и видеоузел. Если один из них заблокирован напрямую,
    /// стратегия обязана его открыть, даже если зато открывает много других сайтов.
    /// </summary>
    public static bool IsCritical(string? site)
    {
        if (string.IsNullOrWhiteSpace(site)) return false;
        if (IsVideoNode(site)) return IsAvailable;
        var s = site.Trim().ToLowerInvariant();
        if (s.StartsWith("https://")) s = s[8..];
        s = s.TrimEnd('/');
        return s is "youtube.com" or "www.youtube.com" or "youtubei.googleapis.com";
    }

    public static bool IsAvailable => (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && File.Exists(CurlPath);

    public static bool IsVideoNode(string? site)
    {
        if (string.IsNullOrWhiteSpace(site)) return false;
        var s = site.Trim().ToLowerInvariant();
        if (s.StartsWith("https://")) s = s[8..];
        s = s.TrimEnd('/');
        return s.StartsWith("rr") && s.Contains("---sn-") && s.EndsWith(".googlevideo.com");
    }

    public static async Task<SiteResult> CheckAsync(string site, int? socksPort, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var seconds = Math.Max(2, (int)Math.Ceiling(timeout.TotalSeconds));
        var args = new List<string>
        {
            "-sS", "-o", NullDevice, "-m", seconds.ToString(), "--connect-timeout", seconds.ToString(),
            "-A", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
            "-w", "%{http_code}",
        };

        args.AddRange(PlatformArgs);

        string url;
        // Через обход проверяем настоящий видеоузел провайдера (адрес берётся со страницы видео):
        // фиктивное имя на IP Google проходит даже через стратегии, при которых видео не грузится.
        if (IsVideoNode(site) && socksPort != null && await FindRealNodeAsync(socksPort.Value, ct) is { } real)
        {
            args.AddRange(new[] { "-k", "--socks5-hostname", $"127.0.0.1:{socksPort}" });
            url = $"https://{real}/";
            args.Add(url);
            var (e, c) = await RunAsync(args, timeout + TimeSpan.FromSeconds(3), ct);
            var good = e == 0 && c != "000";
            return new SiteResult(site, good, sw.Elapsed, good ? null : Describe(e));
        }
        if (IsVideoNode(site))
        {
            var host = site.Trim().ToLowerInvariant();
            if (host.StartsWith("https://")) host = host[8..];
            host = host.TrimEnd('/');
            IPAddress? ip;
            try
            {
                ip = (await Dns.GetHostAddressesAsync(VideoAddressHost, AddressFamily.InterNetwork, ct)).FirstOrDefault();
            }
            catch (SocketException)
            {
                ip = null;
            }
            if (ip == null) return new SiteResult(site, false, sw.Elapsed, "не удалось получить адрес googlevideo");
            // Любой HTTP-код (403/404) значит, что TLS прошёл. Сертификат не важен, проверяется только рукопожатие.
            args.AddRange(new[] { "-k", "--resolve", $"{host}:443:{ip}" });
            // Адрес уже известен, поэтому прокси получает IP (--socks5), а не имя, которого не существует.
            if (socksPort != null) args.AddRange(new[] { "--socks5", $"127.0.0.1:{socksPort}" });
            url = $"https://{host}/";
        }
        else
        {
            url = site.StartsWith("http://") || site.StartsWith("https://") ? site : "https://" + site;
            args.AddRange(new[] { "-L", "--max-redirs", "5", "-r", "0-65535" });
            if (socksPort != null) args.AddRange(new[] { "--socks5-hostname", $"127.0.0.1:{socksPort}" });
        }
        args.Add(url);

        var (exit, code) = await RunAsync(args, timeout + TimeSpan.FromSeconds(3), ct);
        var ok = IsVideoNode(site)
            ? exit == 0 && code != "000"
            : (exit == 0 || exit == 63) && code != "000";
        return new SiteResult(site, ok, sw.Elapsed, ok ? null : Describe(exit));
    }

    private static readonly SemaphoreSlim DiscoveryLock = new(1, 1);
    private static string? _cachedNode;
    private static DateTime _cachedAt;

    /// <summary>
    /// Ищет живой видеоузел (rrN---sn-….googlevideo.com) на странице ролика, скачанной через SOCKS-порт.
    /// Результат кэшируется на 30 минут. Если страница не открылась, возвращает null: стратегия и так негодная.
    /// </summary>
    internal static async Task<string?> FindRealNodeAsync(int socksPort, CancellationToken ct)
    {
        if (_cachedNode != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30)) return _cachedNode;
        await DiscoveryLock.WaitAsync(ct);
        try
        {
            if (_cachedNode != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(30)) return _cachedNode;
            var psi = new ProcessStartInfo(CurlPath)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "-sS", "-m", "12", "--connect-timeout", "6", "-L",
                "-A", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
                "-H", "Accept-Language: en", "-b", "CONSENT=YES+1; SOCS=CAI",
                "--socks5-hostname", $"127.0.0.1:{socksPort}",
                "https://www.youtube.com/watch?v=jNQXAC9IVRw",
            }) psi.ArgumentList.Add(a);
            foreach (var a in PlatformArgs) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            string html;
            try
            {
                var read = proc.StandardOutput.ReadToEndAsync(cts.Token);
                _ = proc.StandardError.ReadToEndAsync(cts.Token);
                html = await read;
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return null;
            }
            var node = System.Text.RegularExpressions.Regex
                .Matches(html, @"rr\d+---sn-[a-z0-9-]+\.googlevideo\.com")
                .Select(m => m.Value)
                .GroupBy(x => x)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            if (node != null)
            {
                _cachedNode = node;
                _cachedAt = DateTime.UtcNow;
                Log.Info("Автоподбор", "Видеоузел YouTube для проверки: " + node);
            }
            return node;
        }
        finally
        {
            DiscoveryLock.Release();
        }
    }

    private static async Task<(int Exit, string Code)> RunAsync(List<string> args, TimeSpan limit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(CurlPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить curl");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        try
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            _ = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return (proc.ExitCode, (await stdout).Trim());
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return (28, "000");
        }
    }

    private static string Describe(int exit) => exit switch
    {
        6 => "имя не найдено",
        7 => "нет соединения",
        28 => "таймаут",
        35 or 60 => "ошибка TLS",
        18 or 52 or 56 => "ответ оборвался",
        97 => "прокси не отвечает",
        _ => $"curl: код {exit}",
    };
}
