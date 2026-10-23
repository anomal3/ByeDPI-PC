using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using ByeDpiPc.Core.Engine;
using ByeDpiPc.Core.Routing;

namespace ByeDpiPc.Core.Testing;

public sealed record DiagnosticItem(bool? Ok, string Title, string Details);

/// <summary>«Диагностика»: проверки понятным языком.</summary>
public static class Diagnostics
{
    public static async Task<List<DiagnosticItem>> RunAsync(AppSettings s, ConnectionManager connection, CancellationToken ct)
    {
        var items = new List<DiagnosticItem>();

        var ciadpi = CiadpiEngine.FindExecutable();
        items.Add(new(ciadpi != null, "Движок ciadpi", ciadpi ?? "Не найден. Положите ciadpi в папку engine рядом с программой."));
        var singBox = TunController.FindExecutable();
        items.Add(new(singBox != null || s.Mode == EngineMode.ProxyOnly, "Движок sing-box (для TUN)",
            singBox ?? "Не найден. Без него работает только режим «Только прокси»."));
        if (OperatingSystem.IsWindows())
        {
            var winws = ZapretController.FindExecutable();
            items.Add(new(winws != null || s.Engine != EngineKind.Zapret, "Движок zapret (winws)",
                winws ?? "Не найден. Скачайте сборку программы целиком или выберите движок byedpi."));
        }

        var vpn = VpnDetector.ActiveVpnAdapters();
        items.Add(new(vpn.Count == 0, "Другие VPN",
            vpn.Count == 0 ? "Не найдены" : VpnDetector.Warning()!));

        items.Add(new(connection.IsConnected, "Подключение",
            connection.IsConnected
                ? $"Подключено, движок: {connection.ModeTitle}"
                : connection.ErrorMessage ?? "Сейчас не подключено — часть проверок пропущена."));

        if (connection.IsConnected && connection.ActiveEngine == EngineKind.ByeDpi)
        {
            var listening = NetUtil.IsPortBusy(s.SocksPort);
            items.Add(new(listening, $"Прокси 127.0.0.1:{s.SocksPort}", listening ? "Принимает подключения" : "Порт не слушается — ciadpi не работает"));
            if (connection.ActiveMode == EngineMode.Tun)
            {
                var tun = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Contains(SingBoxConfigBuilder.TunInterfaceName, StringComparison.OrdinalIgnoreCase));
                items.Add(new(tun != null && tun.OperationalStatus == OperationalStatus.Up, "Адаптер TUN",
                    tun != null ? $"{tun.Name}: {tun.OperationalStatus}" : "Адаптер не найден"));
            }
        }

        try
        {
            var addrs = await Dns.GetHostAddressesAsync("youtube.com", ct);
            items.Add(new(addrs.Length > 0, "DNS", "youtube.com → " + string.Join(", ", addrs.Take(3).Select(a => a.ToString()))));
        }
        catch (Exception ex)
        {
            items.Add(new(false, "DNS", "Не удалось получить адрес youtube.com: " + ex.Message));
        }

        var sites = s.TestSites.Take(8).ToList();
        var timeout = TimeSpan.FromSeconds(Math.Max(3, s.TestTimeoutSec));
        if (!(connection.IsConnected && connection.ActiveEngine == EngineKind.Zapret))
        {
            using var direct = new SiteChecker(null, timeout);
            var r = await direct.CheckAllAsync(sites, 8, ct);
            items.Add(new(null, "Сайты напрямую", Describe(r)));
        }
        if (connection.IsConnected && (connection.ActiveEngine == EngineKind.Zapret || NetUtil.IsPortBusy(s.SocksPort)))
        {
            // zapret перехватывает трафик сам — проверяем «напрямую»; byedpi — через SOCKS.
            using var via = new SiteChecker(connection.ActiveEngine == EngineKind.Zapret ? null : s.SocksPort, timeout);
            var r = await via.CheckAllAsync(sites, 8, ct);
            items.Add(new(r.Count(x => x.Ok) >= r.Count(x => !x.Ok), "Сайты через обход", Describe(r) +
                (r.Any(x => !x.Ok) ? "\nЕсли многие сайты не открываются — подберите другую стратегию на вкладке «Подбор»." : "")));
        }
        return items;
    }

    private static string Describe(List<SiteResult> results)
    {
        var sb = new StringBuilder();
        sb.Append($"Открылось {results.Count(r => r.Ok)} из {results.Count}");
        var failed = results.Where(r => !r.Ok).ToList();
        if (failed.Count > 0) sb.Append(". Не открылись: " + string.Join(", ", failed.Select(f => f.Site)));
        return sb.ToString();
    }

    public static string ToText(IEnumerable<DiagnosticItem> items) =>
        string.Join("\n", items.Select(i => $"[{(i.Ok == null ? "i" : i.Ok.Value ? "OK" : "!!")}] {i.Title}: {i.Details}"));
}
