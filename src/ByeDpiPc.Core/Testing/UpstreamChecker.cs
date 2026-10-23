using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Routing;

namespace ByeDpiPc.Core.Testing;

/// <summary>
/// Проверка внешнего прокси: SOCKS/HTTP — напрямую через HttpClient, VLESS и SSH — через временный
/// локальный мост sing-box (без прав администратора).
/// </summary>
public static class UpstreamChecker
{
    public static async Task<List<SiteResult>> CheckAsync(UpstreamProxy proxy, IReadOnlyList<string> sites, CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(10);
        if (!proxy.NeedsLocalBridge)
        {
            using var direct = new SiteChecker(null, timeout, proxy.ToUri());
            return await direct.CheckAllAsync(sites, sites.Count, ct);
        }

        var exe = TunController.RequireExecutable();
        var port = NetUtil.GetFreePort();
        var config = Path.Combine(AppPaths.RuntimeDir, $"check-{port}.json");
        await File.WriteAllTextAsync(config, SingBoxConfigBuilder.BuildLocalBridge(proxy, port), ct);
        var lines = new List<string>();
        using var bridge = ChildProcess.Start(exe, new[] { "run", "-c", config, "--disable-color" }, "sing-box (проверка)", logOutput: false);
        bridge.Output += l => { lock (lines) lines.Add(l); };
        try
        {
            if (!await NetUtil.WaitForPortAsync(port, TimeSpan.FromSeconds(8), () => bridge.HasExited, ct))
            {
                string text;
                lock (lines) text = string.Join("\n", lines.TakeLast(5));
                throw new UserFacingException("Не удалось запустить мост к внешнему прокси.", text);
            }
            using var checker = new SiteChecker(port, timeout);
            return await checker.CheckAllAsync(sites, sites.Count, ct);
        }
        finally
        {
            await bridge.StopAsync();
            try { File.Delete(config); } catch { }
        }
    }
}
