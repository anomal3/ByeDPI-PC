using System.Diagnostics;
using System.Net;

namespace ByeDpiPc.Core.Testing;

public sealed record SiteResult(string Site, bool Ok, TimeSpan Elapsed, string? Error);

/// <summary>
/// Проверка доступности сайтов напрямую или через SOCKS5.
/// По смыслу — SiteCheckUtils из ByeByeDPI: сайт доступен, если пришёл HTTP-ответ и тело дочиталось до конца
/// (DPI часто рвёт соединение посреди ответа).
/// https://github.com/romanvht/ByeByeDPI/blob/master/app/src/main/java/io/github/romanvht/byedpi/utility/SiteCheckUtils.kt
/// </summary>
public sealed class SiteChecker : IDisposable
{
    private const int ReadLimit = 64 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public SiteChecker(int? socksPort, TimeSpan timeout, Uri? proxy = null)
    {
        _timeout = timeout;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = timeout,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.Zero,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        };
        if (proxy != null || socksPort != null)
        {
            handler.Proxy = new WebProxy(proxy ?? new Uri($"socks5://127.0.0.1:{socksPort}"));
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public async Task<SiteResult> CheckAsync(string site, CancellationToken ct)
    {
        var url = site.StartsWith("http://") || site.StartsWith("https://") ? site : "https://" + site;
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var declared = resp.Content.Headers.ContentLength;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[8192];
            long total = 0;
            var limit = declared is > 0 ? Math.Min(declared.Value, ReadLimit) : ReadLimit;
            while (total < limit)
            {
                var n = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, limit - total)), cts.Token);
                if (n == 0) break;
                total += n;
            }
            if (declared is > 0 && total < Math.Min(declared.Value, ReadLimit))
                return new SiteResult(site, false, sw.Elapsed, "ответ оборвался");
            return new SiteResult(site, true, sw.Elapsed, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SiteResult(site, false, sw.Elapsed, "таймаут");
        }
        catch (HttpRequestException ex)
        {
            return new SiteResult(site, false, sw.Elapsed, Simplify(ex));
        }
        catch (IOException ex)
        {
            return new SiteResult(site, false, sw.Elapsed, ex.Message);
        }
    }

    public async Task<List<SiteResult>> CheckAllAsync(IEnumerable<string> sites, int concurrency, CancellationToken ct, Action<SiteResult>? onResult = null)
    {
        using var sem = new SemaphoreSlim(concurrency);
        var tasks = sites.Select(async site =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var r = await CheckAsync(site, ct);
                onResult?.Invoke(r);
                return r;
            }
            finally
            {
                sem.Release();
            }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static string Simplify(HttpRequestException ex)
    {
        var inner = ex.InnerException?.Message ?? ex.Message;
        return inner.Length > 120 ? inner[..120] + "…" : inner;
    }

    public void Dispose() => _http.Dispose();
}
