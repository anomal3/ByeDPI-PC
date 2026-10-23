using System.Text.Json;
using System.Text.Json.Nodes;

namespace ByeDpiPc.Core.Routing;

/// <summary>
/// Генерирует конфиг sing-box (проверено на 1.14.x): TUN → правила → direct / socks(ciadpi) / upstream.
/// Это не VPN: трафик никуда не уходит, «обходный» поток просто проходит через локальный ciadpi.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string TunInterfaceName = "ByeDPI";

    /// <summary>Свои процессы всегда идут напрямую — иначе получится петля.</summary>
    public static readonly string[] SelfProcesses = { "ciadpi", "sing-box", "ByeDPI-PC" };

    public static string Build(AppSettings s, RoutePlan plan, bool? windows = null)
    {
        var isWindows = windows ?? OperatingSystem.IsWindows();
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = BuildDns(s),
            ["inbounds"] = new JsonArray(BuildTun(s)),
            ["outbounds"] = BuildOutbounds(s, plan),
            ["route"] = BuildRoute(s, plan, isWindows),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildDns(AppSettings s)
    {
        var remote = DnsServer(s);
        remote["tag"] = "remote";
        return new JsonObject
        {
            ["servers"] = new JsonArray(remote, new JsonObject { ["tag"] = "local", ["type"] = "local" }),
            ["final"] = "remote",
            ["strategy"] = s.Ipv6 ? "prefer_ipv4" : "ipv4_only",
        };
    }

    internal static JsonObject DnsServer(AppSettings s)
    {
        switch (s.Dns)
        {
            case DnsPreset.Google: return new JsonObject { ["type"] = "https", ["server"] = "8.8.8.8" };
            case DnsPreset.Quad9: return new JsonObject { ["type"] = "https", ["server"] = "9.9.9.9" };
            case DnsPreset.System: return new JsonObject { ["type"] = "local" };
            case DnsPreset.Custom when !string.IsNullOrWhiteSpace(s.CustomDns):
                var text = s.CustomDns.Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "tls" or "quic" or "h3" or "udp" or "tcp")
                {
                    var o = new JsonObject { ["type"] = uri.Scheme, ["server"] = uri.Host };
                    if (!uri.IsDefaultPort && uri.Port > 0) o["server_port"] = uri.Port;
                    if (uri.Scheme is "https" or "h3" && uri.AbsolutePath is { Length: > 1 } path) o["path"] = path;
                    return o;
                }
                return new JsonObject { ["type"] = "udp", ["server"] = text };
            default: return new JsonObject { ["type"] = "https", ["server"] = "1.1.1.1" };
        }
    }

    private static JsonObject BuildTun(AppSettings s)
    {
        var address = new JsonArray("172.19.0.1/30");
        if (s.Ipv6) address.Add("fdfe:dcba:9876::1/126");
        return new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = TunInterfaceName,
            ["address"] = address,
            ["mtu"] = 9000,
            ["auto_route"] = true,
            ["strict_route"] = true,
            ["stack"] = "mixed",
        };
    }

    private static JsonArray BuildOutbounds(AppSettings s, RoutePlan plan)
    {
        var list = new JsonArray(
            new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
            new JsonObject
            {
                ["type"] = "socks", ["tag"] = "byedpi", ["server"] = "127.0.0.1",
                ["server_port"] = s.SocksPort, ["version"] = "5",
            });
        if (plan.UpstreamProxy is { } u)
        {
            var o = new JsonObject
            {
                ["type"] = u.Kind == UpstreamKind.Socks5 ? "socks" : "http",
                ["tag"] = "upstream",
                ["server"] = u.Host,
                ["server_port"] = u.Port,
            };
            if (u.Kind == UpstreamKind.Socks5) o["version"] = "5";
            if (u.User != null) o["username"] = u.User;
            if (u.Password != null) o["password"] = u.Password;
            if (u.Kind == UpstreamKind.Https) o["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = u.Host };
            list.Add(o);
        }
        return list;
    }

    private static JsonObject BuildRoute(AppSettings s, RoutePlan plan, bool windows)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["process_name"] = ToArray(ProcessNames(SelfProcesses, windows)), ["outbound"] = "direct" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
        };

        // Порядок: явный «Напрямую» → внешний прокси → обход DPI.
        AddTargets(rules, plan.Direct, "direct", windows, blockQuic: false);
        if (plan.UpstreamProxy != null) AddTargets(rules, plan.Upstream, "upstream", windows, blockQuic: false);
        AddTargets(rules, plan.ByeDpi, "byedpi", windows, s.BlockQuic);

        if (plan.Final == RouteMode.ByeDpi && s.BlockQuic)
            rules.Add(new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });

        return new JsonObject
        {
            ["rules"] = rules,
            ["final"] = plan.Final == RouteMode.Direct ? "direct" : "byedpi",
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "local",
        };
    }

    /// <summary>
    /// В одном правиле sing-box поля domain/ip_cidr и process_name объединяются через «И»,
    /// поэтому каждую группу кладём отдельным правилом.
    /// </summary>
    private static void AddTargets(JsonArray rules, RouteTargets t, string outbound, bool windows, bool blockQuic)
    {
        var groups = new List<(string Key, JsonArray Values)>();
        if (t.Processes.Count > 0) groups.Add(("process_name", ToArray(ProcessNames(t.Processes, windows))));
        if (t.Domains.Count > 0) groups.Add(("domain_suffix", ToArray(t.Domains)));
        if (t.IpCidrs.Count > 0) groups.Add(("ip_cidr", ToArray(t.IpCidrs)));

        if (blockQuic)
        {
            foreach (var (key, values) in groups)
                rules.Add(new JsonObject
                {
                    ["network"] = "udp", ["port"] = 443, [key] = values.DeepClone(), ["action"] = "reject",
                });
        }
        foreach (var (key, values) in groups)
            rules.Add(new JsonObject { [key] = values, ["outbound"] = outbound });
    }

    internal static IEnumerable<string> ProcessNames(IEnumerable<string> names, bool windows)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in names)
        {
            var n = raw.Trim();
            if (n.Length == 0) continue;
            var hasExe = n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (windows) set.Add(hasExe ? n : n + ".exe");
            else set.Add(hasExe ? n[..^4] : n);
        }
        return set;
    }

    private static JsonArray ToArray(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
}
