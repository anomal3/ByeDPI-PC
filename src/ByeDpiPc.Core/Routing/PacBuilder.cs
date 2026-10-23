using System.Text;
using System.Text.Json;

namespace ByeDpiPc.Core.Routing;

/// <summary>
/// PAC-файл для режима «Только прокси»: браузеры отправляют в ciadpi только домены из правил.
/// Правила по программам в этом режиме невозможны (у системного прокси нет понятия «процесс»).
/// </summary>
public static class PacBuilder
{
    public static string Build(RoutePlan plan, int socksPort)
    {
        var byedpi = $"SOCKS5 127.0.0.1:{socksPort}; SOCKS 127.0.0.1:{socksPort}";
        var upstream = plan.UpstreamProxy?.PacEntry ?? byedpi;
        var final = plan.Final == RouteMode.Direct ? "DIRECT" : byedpi;

        var sb = new StringBuilder();
        sb.AppendLine("// Сгенерировано ByeDPI PC. Не редактируйте — файл перезаписывается.");
        sb.AppendLine("var direct = " + JsonArray(plan.Direct.Domains) + ";");
        sb.AppendLine("var upstream = " + JsonArray(plan.Upstream.Domains) + ";");
        sb.AppendLine("var byedpi = " + JsonArray(plan.ByeDpi.Domains) + ";");
        sb.AppendLine("""
            function inList(host, list) {
              for (var i = 0; i < list.length; i++) {
                var d = list[i];
                if (host === d) return true;
                var n = host.length - d.length;
                if (n > 0 && host.charAt(n - 1) === "." && host.substring(n) === d) return true;
              }
              return false;
            }
            function FindProxyForURL(url, host) {
              host = host.toLowerCase();
              if (isPlainHostName(host) || host === "localhost" || shExpMatch(host, "127.*") ||
                  shExpMatch(host, "10.*") || shExpMatch(host, "192.168.*") || shExpMatch(host, "*.local")) return "DIRECT";
              if (inList(host, direct)) return "DIRECT";
            """);
        sb.AppendLine($"  if (inList(host, upstream)) return \"{upstream}\";");
        sb.AppendLine($"  if (inList(host, byedpi)) return \"{byedpi}\";");
        sb.AppendLine($"  return \"{final}\";");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string JsonArray(IEnumerable<string> values) => JsonSerializer.Serialize(values.ToArray());
}
